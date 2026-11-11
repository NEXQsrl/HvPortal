<p align="center"><a href="INSTALL.md">English</a> | <b>Italiano</b></p>

# Installare HvPortal

Questa guida installa HvPortal 0.7.0 sui tuoi server: il **gateway** (il portale web) su un server di management e un
**agent** su ogni host Hyper-V. Tratta anche certificati, aggiornamenti e disinstallazione, e spiega che cosa deve
essere pronto in Hyper-V prima dei backup e della migrazione in tempo reale. Il riferimento con tutte le opzioni è
[docs/install.md](docs/install.md) (in inglese).

> HvPortal è in anteprima. Le verifiche ancora da eseguire su host reali sono elencate nel
> [piano di test su host reali](docs/verification/real-host-test-plan.md). Installalo prima in laboratorio o su host
> non critici.

## Indice

- [Panoramica](#panoramica)
- [Requisiti](#requisiti)
- [1. Procurarsi i file della release](#1-procurarsi-i-file-della-release)
- [2. Installare il gateway](#2-installare-il-gateway)
- [3. Primo accesso](#3-primo-accesso)
- [4. Certificato del portale e nomi](#4-certificato-del-portale-e-nomi)
- [5. Installare l'agent su ogni host Hyper-V](#5-installare-lagent-su-ogni-host-hyper-v) (5i: cluster di failover)
- [6. Accesso con Microsoft Entra ID (facoltativo)](#6-accesso-con-microsoft-entra-id-facoltativo)
- [7. Prima del primo backup](#7-prima-del-primo-backup)
- [8. Prima di spostare le VM tra host](#8-prima-di-spostare-le-vm-tra-host)
- [9. Aggiornare](#9-aggiornare)
- [10. Disinstallare](#10-disinstallare)
- [11. Risoluzione dei problemi](#11-risoluzione-dei-problemi)

## Panoramica

| Componente | Dove | Che cos'è |
|---|---|---|
| Gateway | Un server o una VM di management (Windows Server), oppure un host Docker | Il servizio Windows `HvPortalGateway`, che gira come `NT SERVICE\HvPortalGateway` e non è amministratore, oppure un container Linux senza root. Offre il portale, l'API REST e il canale degli agent. |
| Agent | Ogni host Hyper-V | Il servizio Windows `HvPortalAgent`, che gira come `NT SERVICE\HvPortalAgent` ed è membro del solo gruppo locale *Hyper-V Administrators*. È lui ad aprire la connessione verso il gateway. |
| Agent di management (facoltativo) | Un server Windows senza Hyper-V | Lo stesso agent, installato in modalità management. Raggiunge host su cui non è installato nulla, con credenziali conservate cifrate nel portale. |

La configurazione consigliata prevede un server o una VM di management dedicati al gateway. Il gateway può anche stare
su un host Hyper-V: in quel caso gateway e agent restano due servizi con due identità separate. Nessun componente
richiede l'appartenenza a un dominio, e lo stesso gateway può gestire host di domini o workgroup diversi.

Porte di rete (entrambi i numeri si possono cambiare durante l'installazione):

| Porta | Da → a | A che cosa serve |
|---|---|---|
| TCP 8443 | Browser → gateway | Portale, API REST, aggiornamenti in tempo reale |
| TCP 8444 | Agent → gateway | Registrazione, canale degli agent (TLS reciproco), download del pacchetto dell'agent |
| Nessuna | → host Hyper-V | È l'agent ad aprire la connessione: sugli host non viene creata alcuna regola in ingresso |

Il traffico verso la porta degli agent deve arrivare direttamente al gateway. Non può passare da un proxy che termina
il TLS, perché l'agent si autentica con un certificato client. Gli agent di management sono l'unica eccezione al "nessuna porta in
ingresso": dal server di management verso ogni host remoto servono WMI/DCOM (TCP 135 e RPC dinamiche), WinRM (5985 o
5986) e SMB (445).

## Requisiti

**Server del gateway**

- Windows Server a 64 bit. La piattaforma di riferimento è Windows Server 2025; Windows Server 2022 è in valutazione
  (vedi la [matrice di compatibilità](docs/compatibility-matrix.md)). Va bene anche una VM.
- Diritti di amministratore per l'installazione. Il servizio poi gira senza.
- La release è autonoma: non serve installare il runtime .NET. Il setup grafico richiede .NET Framework 4.7.2, già
  incluso in Windows Server 2019 e successivi.
- La cartella dei dati (`C:\ProgramData\HvPortal\Gateway` per impostazione predefinita) deve stare su un disco locale,
  non su una condivisione SMB.
- L'elenco dei nomi DNS e degli indirizzi IP con cui browser e agent raggiungeranno il gateway: finiscono nel
  certificato interno. Un host fuori dominio spesso non risolve i nomi del dominio, quindi metti anche l'indirizzo IP.
- Porte TCP libere per il portale e per gli agent. La porta 443 è spesso già occupata da IIS o da un altro listener
  HTTP.sys.

**Oppure un host Docker** (`linux/amd64`) per il gateway: vedi [2c](#2c-docker-container-linux).

**Ogni host Hyper-V**

- Windows Server con il ruolo Hyper-V, Windows PowerShell 5.1 (incluso in Windows) e diritti di amministratore per
  l'installazione.
- Il modulo PowerShell di Hyper-V: `Install-WindowsFeature Hyper-V-PowerShell`. Senza il modulo funzionano comunque
  inventario, alimentazione e console; non funzionano impostazioni delle VM, creazione di VM, checkpoint, template,
  reti, backup, spostamenti e stato di salute dell'host.
- Traffico TCP in uscita verso la porta degli agent del gateway, usando uno dei nomi o indirizzi indicati al gateway.
- Se un criterio di gruppo impone la execution policy PowerShell `AllSigned`, installa una release firmata. La
  modalità PowerShell Constrained Language (imposta da WDAC/AppLocker) non è ancora supportata.

**Browser**: Edge, Chrome o Firefox in versione aggiornata.

**Accesso a Internet**: non serve. Lo usano solo le funzioni che attivi: l'accesso con Microsoft Entra ID (HTTPS dal
gateway verso `login.microsoftonline.com`), il tuo server SMTP e i tuoi webhook per le notifiche.

## 1. Procurarsi i file della release

I setup dell'ultima versione sono nella pagina delle release su GitHub:
[github.com/NEXQsrl/HvPortal/releases/latest](https://github.com/NEXQsrl/HvPortal/releases/latest)
(`HvPortal.Gateway.Setup.exe` e `HvPortal.Agent.Setup.exe`, con il loro SHA-256 nelle note della release; viene tenuta
solo l'ultima versione). Contengono tutto quello che serve ai setup: il setup del gateway porta con sé anche il
pacchetto dell'agent che il portale offre in download. Il certificato pubblico di firma si ricava dalla firma di un
setup, vedi [Firma del codice](#firma-del-codice).

I manutentori pubblicano una release con `.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumb> -PublishGitHub`
(oppure `.\installer\Publish-GitHubRelease.ps1` per una release già compilata): lo script rifiuta file non firmati,
modifiche non committate e un repository pubblico, poi sostituisce tutte le release precedenti e i loro tag.

Puoi anche compilare la release su una macchina Windows con i prerequisiti indicati nel
[README](README_IT.md#compilare-dai-sorgenti).

```powershell
.\installer\Build-Release.ps1                                       # build non firmata
.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumb>  # build firmata
```

Il risultato è in `artifacts\release\<versione>\`:

| Cartella o file | Contenuto |
|---|---|
| `setup\` | `HvPortal.Gateway.Setup.exe` e `HvPortal.Agent.Setup.exe`, i setup grafici |
| `gateway\` | I file del gateway. In `gateway\packages\` ci sono il pacchetto e il setup dell'agent che il portale offre in download. |
| `agent\` | I file dell'agent |
| `installer\` | Gli script PowerShell, più `HvPortal-CodeSigning.cer` in una build firmata |
| `deps\` | L'inventario delle dipendenze di terze parti |
| `SHA256SUMS.txt` | Lo SHA-256 di ogni file |

Copia i file sui server con il tuo abituale canale di distribuzione del software, poi confrontali con
`SHA256SUMS.txt` (`Get-FileHash <file> -Algorithm SHA256`).

### Firma del codice

| Build | Che cosa significa |
|---|---|
| Non firmata | Windows considera i file non firmati. Gli agent si aggiornano solo a mano. |
| Firmata con un certificato self-signed (`installer\New-CodeSigningCertificate.ps1`) | Le firme ci sono e sono integre, ma Windows le segnala come non attendibili finché non importi il certificato. Non danno alcuna reputazione presso SmartScreen o gli antivirus: una firma self-signed non rende attendibile una build. Gli agent si possono aggiornare dal portale. |
| Firmata con un certificato di una CA considerata attendibile dalle tue macchine | Le firme vengono convalidate normalmente. Gli agent si possono aggiornare dal portale. |

Con una build non firmata o self-signed, all'avvio di un setup Windows può mostrare "PC protetto da Windows": controlla
lo SHA-256, poi scegli *Ulteriori informazioni > Esegui comunque*. Perché una firma self-signed venga convalidata,
importa il certificato pubblico su ogni server (PowerShell come amministratore, oppure un GPO):

```powershell
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Se hai solo i setup (release su GitHub), ricava il certificato pubblico dalla firma di un setup e importalo solo se la
sua impronta è quella pubblicata dai manutentori (build di NEXQ S.r.l.: `989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8`):

```powershell
$cert = (Get-AuthenticodeSignature .\HvPortal.Gateway.Setup.exe).SignerCertificate
$cert.Thumbprint
Export-Certificate -Cert $cert -FilePath .\HvPortal-CodeSigning.cer | Out-Null
```

Dopo l'importazione Authenticode segnala i file come validi, i criteri PowerShell `AllSigned` accettano gli script e le
regole di controllo delle applicazioni o dell'antivirus si possono scrivere per editore. Resta il fatto che non dà
alcuna reputazione presso SmartScreen o gli antivirus. Tieni la chiave privata del certificato di firma solo sulla
macchina di build: chi la possiede può firmare codice che queste macchine considereranno attendibile. Senza
`-TimestampServer` le firme smettono di essere valide quando il certificato scade.

## 2. Installare il gateway

### 2a. Setup grafico

1. Copia `HvPortal.Gateway.Setup.exe` sul server del gateway, avvialo e conferma la richiesta dei diritti di
   amministratore.
2. **Indirizzi e porte**: indica tutti i nomi e gli indirizzi IP che useranno i browser e gli agent. La porta del
   portale è 8443; con 443 gli utenti non devono scrivere la porta nell'indirizzo. La porta degli agent è 8444.
3. **Certificato del portale**: lascia il *certificato interno*, scegli un certificato già presente nell'archivio del
   computer oppure *importa un file .pfx* della tua CA (vedi la [sezione 4](#4-certificato-del-portale-e-nomi)).
4. **Windows Firewall**: lascia che il setup crei le regole in ingresso e indica chi può aprire il portale e quali host
   possono collegarsi come agent (`LocalSubnet`, indirizzi o subnet come `10.0.20.0/24`). Senza regole il portale
   risponde solo dal server stesso.
5. **Installa**. Alla fine il setup mostra l'indirizzo del portale e, su una nuova installazione, il token di setup
   monouso.

Il setup è un unico file. Estrae i binari firmati e gli script della release in una cartella in cui possono scrivere
solo gli amministratori, esegue `Install-Gateway.ps1` con le tue scelte e ne mostra l'output. Il log viene salvato in
`C:\ProgramData\HvPortal\SetupLogs`. La procedura è in italiano o in inglese, secondo la lingua di visualizzazione di
Windows.

### 2b. Script PowerShell

Usa lo script per le installazioni automatiche o su Server Core. Gli script sono nella cartella `installer\` della
release, e ogni setup li sa anche estrarre: `HvPortal.Gateway.Setup.exe --extract C:\HvPortal\files`.

```powershell
cd <release>\installer
.\Install-Gateway.ps1 -Source ..\gateway -PublicNames gw01.contoso.local,10.0.10.5 -OpenFirewall `
    -UiRemoteAddress 10.0.10.0/24 -AgentRemoteAddress 10.0.20.0/24
```

| Parametro | Predefinito | Significato |
|---|---|---|
| `-Source` | (obbligatorio) | La cartella `gateway` della release |
| `-PublicNames` | nessuno | Nomi DNS e indirizzi IP usati da browser e agent. Vengono inseriti nel certificato interno. |
| `-UiPort` / `-AgentPort` | 8443 / 8444 | Porta del portale e porta degli agent |
| `-BindAddress` | `0.0.0.0` | Indirizzo su cui restare in ascolto (`0.0.0.0` = tutte le schede di rete) |
| `-UiCertificateThumbprint` | nessuno | Certificato del portale preso da `LocalMachine\My` al posto di quello interno |
| `-OpenFirewall` | disattivato | Crea le regole in ingresso per le due porte (profili firewall Dominio e Privato) |
| `-UiRemoteAddress` / `-AgentRemoteAddress` | `LocalSubnet` | Chi può raggiungere la porta del portale e quella degli agent |
| `-InstallPath` / `-DataPath` | `C:\Program Files\HvPortal\Gateway` / `C:\ProgramData\HvPortal\Gateway` | Binari e dati |
| `-DisableRemoteUpdate` | disattivato | Non registra l'attività di aggiornamento: il gateway non si può aggiornare dal portale ([9](#gateway)). Nel setup grafico: *Consenti l'aggiornamento del portale da Impostazioni > Sistema*. |

Lo script controlla porte e certificato prima di toccare un servizio in esecuzione. Installa il servizio con avvio
automatico ritardato e riavvio in caso di errore. I binari sono in sola lettura per il servizio, e la cartella dei dati
è privata (SYSTEM, Administrators e il servizio). Le impostazioni sono salvate come variabili d'ambiente del servizio
nel registro: `(Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HvPortalGateway).Environment`.

### 2c. Docker (container Linux)

Il gateway può girare come container Linux (ad esempio con Portainer). Gli agent restano servizi Windows sugli host.
La procedura completa è in [docker/README.md](docker/README.md). In breve:

1. Compila l'immagine dal repository; non è pubblicata nessuna immagine pronta:
   `docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.7.0 .`
2. Distribuisci `docker/docker-compose.yml` con le variabili di `docker/.env.example`. Imposta almeno:
   - `HVP_PUBLIC_NAMES`: i nomi e gli indirizzi IP dell'host Docker, così come lo vedono gli host Hyper-V;
   - `HVP_KEY_ENCRYPTION_KEY`: 32 byte casuali in base64 (`openssl rand -base64 32`). **Conservala nel tuo gestore di
     password**: senza di essa il volume dei dati non si può più aprire.
3. Leggi il token di setup nel log del container (riga `HvPortal setup token ...`). Dopo il primo accesso imposta
   `HVP_PRINT_SETUP_TOKEN=false`.

Il container gira con un utente senza root, con il file system radice in sola lettura, senza capability Linux e con
`no-new-privileges`. Pubblica la porta degli agent direttamente verso il container, mai attraverso un proxy che termina
il TLS.

Il pacchetto dell'agent creato dentro l'immagine non è firmato e non ha un catalogo firmato. Gli agent collegati a un
gateway in container si aggiornano quindi a mano ([sezione 9](#9-aggiornare)).

## 3. Primo accesso

1. Apri l'indirizzo mostrato dal setup, ad esempio `https://gw01.contoso.local:8443`. Con il certificato interno il
   browser mostra un avviso: è normale (vedi la [sezione 4](#4-certificato-del-portale-e-nomi)).
2. Inserisci il **token di setup**. Lo mostra il setup grafico, ed è salvato anche in
   `C:\ProgramData\HvPortal\Gateway\bootstrap\setup-token.txt`, che possono leggere solo gli amministratori; in un
   container si trova nel log. Il token vale una sola volta, per 24 ore, e viene sostituito a ogni riavvio finché il
   setup non è completato. Non esistono credenziali predefinite.
3. Scegli il nome utente dell'amministratore e una passphrase di almeno 15 caratteri.
4. Configura un'app di autenticazione (TOTP) e conserva i codici di recupero offline. La verifica in due passaggi è
   obbligatoria per gli amministratori per impostazione predefinita.
5. Proteggi i dati del gateway: **Impostazioni > Backup e ripristino > Crea backup**, con una passphrase di almeno 20
   caratteri. Conserva il file e la passphrase separatamente, offline. Il solo database non basta per un ripristino: le
   chiavi che proteggono i segreti MFA e la CA interna stanno fuori dal database. Su Windows puoi anche eseguire
   `HvPortal.Gateway.exe admin export-keys` (vedi [docs/backup-restore.md](docs/backup-restore.md)).
6. Crea gli utenti e assegna i ruoli in **Utenti e ruoli**. I nuovi utenti non hanno alcun permesso. Preferisci ruoli
   assegnati su un host, un gruppo o una VM a quelli globali.

Se nessuno riesce più ad accedere, un amministratore locale del server del gateway può eseguire
`HvPortal.Gateway.exe admin recover-account --user <nome> [--reset-mfa]` per ottenere un token di recupero monouso.

## 4. Certificato del portale e nomi

Il gateway usa due certificati:

- Il **certificato del portale**, quello che vedono i browser sulla porta del portale. Per impostazione predefinita lo
  emette la CA interna del gateway, e i browser mostrano un avviso finché non consideri attendibile quella CA. Puoi
  scaricare la CA da `https://<gateway>:8444/agent/v1/ca` e importarla tra le autorità radice attendibili dei client.
- Il **certificato del canale degli agent**, emesso sempre dalla CA interna. Durante la registrazione gli agent fissano
  quella CA tramite la sua impronta, e controllano che il nome del gateway sia nel certificato. Ogni nome o indirizzo
  usato da un agent deve quindi essere tra i nomi pubblici del gateway. Per aggiungere un nome in seguito riesegui il
  setup del gateway: il certificato viene emesso di nuovo all'avvio del servizio.

**Usare un certificato della tua CA (e la porta 443)**

- Con il setup grafico: riesegui `HvPortal.Gateway.Setup.exe`. Nella pagina *Certificato del portale* scegli *Importa
  un file di certificato della tua CA (.pfx)* e, se vuoi, cambia la porta nella prima pagina.
- Con lo script: importa il PFX nell'archivio del computer, poi riesegui `Install-Gateway.ps1` con **tutti** i
  parametri usati all'installazione, più l'impronta:

  ```powershell
  $cert = Import-PfxCertificate -FilePath C:\temp\hvportal.pfx -CertStoreLocation Cert:\LocalMachine\My `
      -Password (Read-Host -AsSecureString 'Password del PFX')
  .\Install-Gateway.ps1 -Source ..\gateway -PublicNames hvportal.contoso.com,10.0.10.5 -OpenFirewall `
      -UiPort 443 -UiCertificateThumbprint $cert.Thumbprint
  ```

Il certificato deve contenere i nomi che gli utenti scrivono nel browser (Subject Alternative Name), e la sua CA deve
essere attendibile per i client. L'installer dà al servizio del gateway l'accesso in lettura alla chiave privata. Gli
agent continuano a usare la porta 8444 e la CA interna, quindi sugli host non cambia nulla. Per il rinnovo importa il
nuovo certificato e ripeti il passaggio con la nuova impronta. Se usi l'accesso con Entra ID, registra ogni nuovo
indirizzo del portale come URI di reindirizzamento.

## 5. Installare l'agent su ogni host Hyper-V

### 5a. Aggiungere l'host nel portale

Nel portale scegli **Aggiungi host**, poi **Agent sull'host (consigliato)**. Tieni aperta la finestra, che mostra:

- un **token monouso**, valido 15 minuti (*Nuovo token di registrazione* ne crea un altro), e l'**impronta della CA**;
- i download: `HvPortal.Agent.Setup.exe` e il pacchetto `HvPortal.Agent-win-x64.zip`, con il loro SHA-256 e lo stato
  della firma (i download richiedono il permesso `hosts.manage` e vengono registrati nell'audit);
- il nome del gateway da usare: scegline uno che l'host riesce a raggiungere (`localhost` non funziona mai da un'altra
  macchina);
- lo stato della connessione dell'host, aggiornato in tempo reale.

Prima controlla la rete dall'host: `Test-NetConnection <gateway> -Port 8444`.

### 5b. Setup grafico

1. Nella finestra scegli *Setup guidato*, scarica `HvPortal.Agent.Setup.exe` e copialo sull'host.
2. Copia la riga **Dati per il setup** (indirizzo del gateway, impronta della CA e token). Se la copi da un altro PC e
   gli appunti non arrivano sull'host, incollala in un file di testo sull'host e copiala da lì, oppure scrivi a mano i
   tre valori.
3. Sull'host avvia il setup e conferma la richiesta dei diritti di amministratore:
   - **Dove viene installato l'agent**: *Su un host Hyper-V*.
   - **Collegamento al gateway**: premi **Incolla dal portale**.
   - **Account di servizio**: lascia *Account automatico*, a meno che le tue regole non richiedano un account con nome
     ([5f](#5f-account-di-servizio-dedicato)).
   - **Cartelle di storage**: *Tutti i dischi locali* oppure *Solo queste cartelle* ([5d](#5d-cartelle-di-storage)).
     Nella stessa pagina *Consenti l'aggiornamento dell'agent dal portale* è selezionato per impostazione predefinita
     ([5e](#5e-aggiornamenti-dal-portale)).
4. **Installa**. Entro pochi secondi l'host risulta online nel portale. Controlla che *Accesso Hyper-V* indichi `Ok`.

### 5c. Script PowerShell

```powershell
Get-FileHash .\HvPortal.Agent-win-x64.zip -Algorithm SHA256     # confronta con il valore mostrato nel portale
Unblock-File .\HvPortal.Agent-win-x64.zip
Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package
cd C:\HvPortal\agent-package\installer
$env:HVP_ENROLLMENT_TOKEN = '<token>'       # il token passa in una variabile, non sulla riga di comando dell'installer
.\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <impronta>
```

Il metodo *PowerShell* del portale mostra questi comandi già compilati.

| Parametro | Significato |
|---|---|
| `-Source` | La cartella `agent` del pacchetto (obbligatorio) |
| `-Gateway`, `-CaSha256` | Indirizzo del gateway sulla porta degli agent e impronta della CA. Solo per la registrazione: in un aggiornamento non vanno indicati. |
| `-EnrollmentToken` | Il token monouso. Meglio passarlo con la variabile `HVP_ENROLLMENT_TOKEN`. |
| `-ForceReenroll` | Registra di nuovo l'agent con un nuovo token, sostituendo l'identità attuale |
| `-StorageFolders` | Le cartelle di questo host che il portale può usare ([5d](#5d-cartelle-di-storage)) |
| `-DisableRemoteUpdate` | Nessun aggiornamento dal portale ([5e](#5e-aggiornamenti-dal-portale)) |
| `-ServiceAccount` | Utente locale dedicato al posto dell'account virtuale ([5f](#5f-account-di-servizio-dedicato)) |
| `-Management` | Server di management invece di un host Hyper-V ([5h](#5h-host-senza-agent-agent-di-management)) |
| `-InstallPath` / `-DataPath` | Predefiniti `C:\Program Files\HvPortal\Agent` / `C:\ProgramData\HvPortal\Agent` |

Lo script installa il servizio `HvPortalAgent` come `NT SERVICE\HvPortalAgent` e lo aggiunge a *Hyper-V Administrators*
(il gruppo viene trovato tramite il SID `S-1-5-32-578`, quindi il nome localizzato, ad esempio "Amministratori
Hyper-V", non conta). Il servizio non viene aggiunto ad *Administrators*. Lo script non crea alcuna regola firewall in
ingresso, poi registra e avvia l'agent. In un aggiornamento, se la nuova versione non parte, vengono ripristinati i
binari precedenti.

### 5d. Cartelle di storage

Per impostazione predefinita il portale può usare tutti i dischi locali fissi dell'host, tranne le cartelle di sistema
(`Windows`, `Program Files`, `ProgramData`, `Users` e altre), che sono nascoste e rifiutate.

Con `-StorageFolders` (oppure *Solo queste cartelle* nel setup) il portale può usare **solo** le cartelle indicate e le
loro sottocartelle: in Esplora file e nei selettori di cartelle, e per la posizione delle VM, i nuovi dischi, le
librerie ISO e dei template, i repository di backup, la cartella di appoggio dei backup, le posizioni di ripristino e
le destinazioni degli spostamenti di VM. Il resto dell'host non è visibile.

```powershell
.\Install-Agent.ps1 -Source ..\agent -StorageFolders 'D:\VMs','D:\ISO','E:\Backups'   # agent già installato: senza token
.\Install-Agent.ps1 -Source ..\agent -StorageFolders ''                                # toglie la restrizione
```

- Fino a 16 cartelle locali su dischi fissi, niente cartelle di sistema e niente `..`. Le cartelle mancanti vengono
  create.
- L'elenco viene fatto rispettare dall'agent, sull'host. È salvato in `C:\ProgramData\HvPortal\Agent\storage.json`
  (riservato a SYSTEM, Administrators e al servizio), e **il portale non può modificarlo**. Se il file non è valido,
  l'agent rifiuta ogni operazione sulle cartelle finché non lo correggi con `-StorageFolders`.
- Un aggiornamento senza il parametro mantiene l'elenco attuale.
- Per gli host raggiunti tramite un agent di management non c'è un agent sull'host: l'elenco si imposta nel portale,
  nelle impostazioni dell'host, da parte di un utente con `hosts.manage`.
- L'elenco non concede diritti sul file system. Vedi [5g](#5g-permessi-sulle-cartelle).

### 5e. Aggiornamenti dal portale

Per impostazione predefinita `Install-Agent.ps1` registra l'attività pianificata `\HvPortal\HvPortal Agent Update`.
Gira come SYSTEM, e l'account di servizio dell'agent può solo avviarla, non modificarla. Il suo script viene copiato in
`C:\Program Files\HvPortal\Updater`. La [sezione 9](#9-aggiornare) spiega che cosa controlla prima di installare
qualcosa.

`-DisableRemoteUpdate` rimuove l'attività pianificata: l'agent si potrà aggiornare solo a mano. Nel setup grafico
togli la spunta a *Consenti l'aggiornamento dell'agent dal portale*. **Ripeti la scelta a ogni aggiornamento manuale**:
un aggiornamento senza `-DisableRemoteUpdate` registra di nuovo l'attività, e nel setup la casella è selezionata per
impostazione predefinita.

### 5f. Account di servizio dedicato

L'account virtuale predefinito `NT SERVICE\HvPortalAgent` segue già il privilegio minimo: non ha password, non può fare
accesso e appartiene solo a *Hyper-V Administrators*. Se le tue regole richiedono un account con nome (ad esempio per
l'audit o per regole di controllo delle applicazioni), aggiungi `-ServiceAccount svc-hvportal`, oppure scegli *Utente
locale dedicato* nel setup. L'installer crea allora un utente **locale** e:

- gli assegna una password casuale di 40 caratteri che nessuno vede e che non scade, impostata senza mai comparire su
  una riga di comando (rieseguendo l'installer con `-ServiceAccount` la password viene cambiata);
- lo rende membro del solo gruppo *Hyper-V Administrators* e toglie da quel gruppo l'account virtuale;
- gli concede solo il diritto *Accesso come servizio* e gli nega l'accesso locale, tramite Desktop remoto, dalla rete e
  come processo batch;
- si rifiuta di prendere il controllo di un utente esistente che non ha creato lui.

Gli aggiornamenti senza `-ServiceAccount` mantengono l'account. Sugli host a dominio, un criterio di gruppo che imposta
gli stessi diritti utente prevale sul criterio locale; l'installer lo segnala.

### 5g. Permessi sulle cartelle

Essere amministratore Hyper-V non dà all'account dell'agent il diritto di scrivere nelle tue cartelle. Concedi il
permesso NTFS *Modifica* sulle cartelle in cui il portale crea VM, carica file, tiene i template o scrive i backup:

```powershell
icacls D:\VMs /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
icacls D:\Templates /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
icacls E:\Backups /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
# con un account dedicato: icacls D:\VMs /grant "svc-hvportal:(OI)(CI)M"
```

### 5h. Host senza agent: agent di management

Per gli host su cui non si deve installare nulla (tipicamente host in workgroup), installa l'agent con `-Management`
(oppure *Su un server di management* nel setup) su un server Windows, usando il token di **Inventario > Agent di
management > Nuovo agent di management**. Poi aggiungi ogni host con **Aggiungi host > Tramite un agent di
management**, indicando le sue credenziali di amministratore. Le credenziali sono conservate cifrate nel portale e
l'agent di management le tiene solo in memoria. Gli host devono essere raggiungibili dal server di management con
WinRM, WMI e SMB. La preparazione degli host e la configurazione di TrustedHosts sono in
[docs/install.md](docs/install.md) (sezione sugli agent di management).

L'agent sull'host resta la modalità consigliata, perché non conserva credenziali. Alcune funzioni lo richiedono: ad
esempio, per sfogliare i file di un punto di ripristino serve un host con il proprio agent.

### 5i. Cluster di failover (anche in workgroup, con o senza Storage Spaces Direct)

Installa l'agent su **ogni nodo**: il portale legge il cluster ed esegue la manutenzione dei nodi tramite qualsiasi nodo
il cui agent è connesso, quindi continua a funzionare quando un nodo o il suo agent sono fermi.

1. **Inventario > Aggiungi host > Cluster di failover**: dai un nome al cluster ed elenca i nodi (uno per riga, ad
   esempio `HV01`, `HV02`), oppure seleziona host già presenti nel portale. Ogni nuovo nodo riceve il proprio token e la
   propria guida di installazione; l'elenco mostra ogni nodo come *connesso* appena il suo agent si registra.
2. Su ogni nodo installa l'agent con il comando della sua guida, che include **`-ClusterAccess Full`** (nel setup
   grafico: *Nodo di un cluster di failover*). Viene eseguito `Grant-ClusterAccess` per l'account del servizio
   dell'agent, che resta fuori dagli *Administrators*. `-ClusterAccess ReadOnly` basta per vedere lo stato;
   manutenzione e VM altamente disponibili richiedono `Full`. Su un agent già installato:
   `.\Install-Agent.ps1 -Source ..\agent -ClusterAccess Full`, oppure su un nodo
   `Grant-ClusterAccess -User 'NT SERVICE\HvPortalAgent' -Full`.
3. Sui nodi serve il modulo PowerShell del Failover Clustering (`Install-WindowsFeature RSAT-Clustering-PowerShell`).
4. Le VM altamente disponibili vengono create su un volume condiviso del cluster (`C:\ClusterStorage\...`). Se l'agent è
   limitato con `-StorageFolders`, includi le cartelle dei CSV (ad esempio `C:\ClusterStorage\Volume1`).

La pagina del cluster mostra i problemi con cosa fare, la capacità in caso di perdita di un nodo, i nodi (con *Metti in
manutenzione / Esci dalla manutenzione*), lo storage (CSV, pool, volumi, dischi e riparazioni di Storage Spaces Direct),
quorum e witness, reti e ruoli. La manutenzione viene rifiutata quando il cluster non può fare a meno del nodo (un
altro nodo fermo o sospeso, riparazioni in corso, witness non disponibile con due nodi, VM che non ci starebbero).
Vedi ADR-041 in [docs/decisions.md](docs/decisions.md).

## 6. Accesso con Microsoft Entra ID (facoltativo)

Il portale ha una pagina passo passo: **Guida > Accesso con Microsoft Entra ID**. In breve:

1. In Entra ID registra un'applicazione a tenant singolo di tipo *Web*, con l'URI di reindirizzamento
   `https://<indirizzo del portale>/api/v1/auth/entra/callback` (uno per ogni indirizzo che aprono gli utenti), e crea
   un segreto client.
2. In **Impostazioni > Microsoft Entra ID** inserisci l'ID del tenant, l'ID client e il segreto, premi *Verifica
   connessione*, poi attiva l'accesso.
3. Crea ogni utente Microsoft in **Utenti e ruoli** (tipo *Microsoft*, UPN esatto, ruolo). Gli utenti non creati nel
   portale non possono accedere.
4. Imponi l'MFA a questi utenti con l'accesso condizionale di Entra ID. Prima di limitare l'accesso locale tieni almeno
   un amministratore locale per le emergenze.

Il gateway ha bisogno di HTTPS in uscita verso `login.microsoftonline.com`.

## 7. Prima del primo backup

Backup, ripristini e test di ripristino girano sugli host, con il modulo PowerShell di Hyper-V. Un backup crea un
checkpoint della VM, lo esporta nel repository, rimuove il checkpoint (i suoi dischi differenziali vengono riuniti),
calcola lo SHA-256 di ogni file esportato e scrive per ultimo un manifest: una cartella senza manifest è un punto di
ripristino incompleto. Ogni punto di ripristino è una copia completa, quindi dimensiona il repository di conseguenza.

**Lista di controllo**

| Che cosa | Perché |
|---|---|
| Agent 0.7.0 o successivo sugli host che eseguono backup o ripristini | Gli agent precedenti non conoscono queste operazioni; il portale chiede di aggiornare l'agent. |
| Modulo PowerShell di Hyper-V sull'host (`Export-VMSnapshot`, `Import-VM`, `Mount-VHD`) | Serve per esportazione, importazione e ripristino dei file. |
| Solo per *Sfoglia i file* di un punto di ripristino: l'agent installato con `-FileRestore Enabled` (nel setup: *Ripristino dei file dai backup* nella pagina dell'account di servizio; su un agent già installato `.\Install-Agent.ps1 -Source ..\agent -FileRestore Enabled`) | Collegare un disco di backup all'host richiede il diritto Windows *Esecuzione attività di manutenzione volume*, che *Hyper-V Administrators* non dà. Consente l'accesso diretto ai volumi, quindi viene concesso solo dove lo chiedi; `-FileRestore Disabled` lo toglie. Senza, il portale rifiuta subito e lo spiega. |
| Checkpoint abilitati su ogni VM (tipo di checkpoint diverso da *Disabilitato*), ad esempio `Set-VM -Name <vm> -CheckpointType Production` | Un backup ha bisogno di un checkpoint. Se il job di backup chiede un tipo diverso, l'impostazione della VM viene cambiata solo per il momento del checkpoint e poi rimessa com'era. |
| Il servizio di integrazione *Backup (copia shadow del volume)* abilitato per la VM, e i servizi di integrazione attivi nel guest | Con un checkpoint di produzione il guest partecipa (VSS nei guest Windows). Altrimenti la copia ripiega su un checkpoint standard, coerente solo come dopo un arresto improvviso (crash-consistent); con *solo produzione* il backup invece fallisce. |
| Un repository in cui l'host può scrivere | Vedi sotto. |
| Una **cartella di appoggio dei backup** sull'host (impostazioni dell'host) | Serve per le condivisioni con credenziali, per i test di ripristino (la VM di prova viene creata in `<appoggio>\restore-tests`) e per sfogliare i file dei punti di ripristino su quelle condivisioni. Deve essere una cartella locale, non la radice di un disco, con spazio per una copia completa della VM più grande. |
| Spazio | Il controllo di preparazione avvisa sotto i 50 GB liberi sul disco di un repository locale e sotto i 100 GB nella cartella di appoggio. |
| Cartelle di storage | Se l'host ha `-StorageFolders`, i repository locali, la cartella di appoggio e le posizioni di ripristino devono stare dentro l'elenco. |

**Repository**

- **Cartella locale di un host** (ad esempio `E:\Backups`): appartiene a quell'host. L'account dell'agent deve poterla
  creare e scriverci ([5g](#5g-permessi-sulle-cartelle)).
- **Condivisione SMB senza credenziali** (ad esempio `\\nas01\backups\hyperv`): la possono usare tutti gli host. L'host
  la raggiunge con il proprio **account computer** (`HOST$`), quindi concedi agli account computer degli host il
  permesso di scrittura sulla condivisione e sulle sue cartelle. Servono host e file server nello stesso dominio (o in
  domini in relazione di trust).
- **Condivisione SMB con credenziali** (ad esempio un NAS in workgroup): nome utente e password sono conservati cifrati
  nel portale e inviati all'agent solo per l'operazione in corso. Hyper-V non può usare queste credenziali, quindi
  l'esportazione viene preparata prima nella cartella di appoggio dell'host e poi copiata sulla condivisione. Per
  questo serve una cartella di appoggio su ogni host che usa il repository.

**Controllo di preparazione**: prima del primo backup fai controllare l'host dal portale (insieme al repository che
intendi usare). Il controllo verifica i cmdlet di Hyper-V, l'impostazione dei checkpoint e i servizi di integrazione
di ogni VM, se il repository è raggiungibile e scrivibile, lo spazio libero e la cartella di appoggio, e per ogni
problema propone una soluzione. Lo stesso controllo è disponibile dall'API:
`GET /api/v1/hosts/{id}/backup-readiness?repositoryId=<id>`.

**Permessi**: `backup.manage` (globale) gestisce repository e job di backup ed elimina i punti di ripristino;
`backup.run` esegue e verifica i backup; `backup.restore` ripristina VM, dischi e file. Un ripristino come nuova VM
richiede anche `vm.create` sull'host di destinazione. Il ruolo predefinito *Backup operator* li comprende.

**Buono a sapersi**

- Il ripristino **sopra la VM originale** spegne la VM e la rimuove da Hyper-V, rinomina i suoi file disco
  (`*.pre-restore-<ora>`) e importa il punto di ripristino con l'id, il nome e le cartelle originali. Per confermare
  devi scrivere il nome della VM. I file rinominati restano sul disco finché non li elimini.
- Il **ripristino di singoli file** monta un disco del punto di ripristino in sola lettura su un host che ha l'agent
  installato. Solo l'utente che lo ha montato può sfogliarlo, ogni download viene registrato nell'audit e il disco
  viene scollegato quando hai finito o dopo 2 ore di inattività. Valgono i permessi dei file del guest: i file che
  l'account dell'agent non può leggere vengono segnalati come tali.
- Il backup di una VM grande può durare ore. Il limite è di 12 ore per VM.

## 8. Prima di spostare le VM tra host

HvPortal sposta le VM con i comandi di Hyper-V: `Move-VM` verso un altro host (migrazione in tempo reale se la VM è
accesa) e `Move-VMStorage` verso un'altra cartella dello stesso host. **Il portale non modifica le impostazioni di
migrazione degli host**: se manca qualcosa, l'attività fallisce con l'errore riportato da Hyper-V.

**Nel portale**

- Agent 0.7.0 o successivo sull'host di origine. La destinazione deve essere un host del portale che ha già inviato il
  proprio inventario, perché il suo nome DNS viene preso da lì.
- L'utente ha bisogno di `vm.migrate` sulla VM e di `vm.create` sull'host di destinazione. Valgono i limiti del gruppo
  della VM.
- La cartella di destinazione deve stare dentro le cartelle di storage dell'host di destinazione, se ne ha. La VM va in
  `<cartella>\<nome della VM>`.

**Sugli host Hyper-V (migrazione in tempo reale)**

- Migrazione in tempo reale attiva su **entrambi** gli host: *Impostazioni Hyper-V > Migrazioni in tempo reale*,
  oppure `Enable-VMMigration`, con le reti da usare (`Set-VMMigrationNetwork`, oppure
  `Set-VMHost -UseAnyNetworkForMigration $true`).
- Un metodo di autenticazione che funzioni tra i due host:
  - **Kerberos con delega vincolata** (dominio): sull'oggetto computer di ogni host in Active Directory consenti la
    delega verso i servizi *Microsoft Virtual System Migration Service* e *cifs* dell'altro host (*cifs* serve per
    spostare anche i file), poi `Set-VMHost -VirtualMachineMigrationAuthenticationType Kerberos`;
  - oppure **CredSSP**.
- Tra i requisiti di Microsoft per la migrazione in tempo reale senza cluster di failover ci sono host nello stesso
  dominio Active Directory, o in domini in relazione di trust.
- Switch virtuali con gli stessi nomi sulla destinazione, processori compatibili (stesso produttore; attiva la
  compatibilità del processore sulla VM se le generazioni sono diverse) e spazio libero sufficiente sulla destinazione.
- Senza *Sposta anche i file della VM*, i file devono già trovarsi su uno storage raggiungibile da entrambi gli host
  (una condivisione SMB o un volume di cluster).

**Spostamento dei file sullo stesso host**: la cartella di destinazione non deve esistere oppure deve essere vuota.
Una VM accesa continua a funzionare durante lo spostamento.

> **Non ancora validato su host reali.** Lo spostamento viene avviato dal servizio dell'agent, non da un amministratore
> che ha fatto accesso. Restano da validare su host reali due punti. Primo: con CredSSP Microsoft richiede che la
> migrazione parta da una sessione aperta sull'host di origine, che un servizio non ha; il metodo adatto è Kerberos con
> delega vincolata. Secondo: verifica quale identità vede l'host di destinazione per l'agent (in rete l'account virtuale
> dell'agent si presenta come l'account computer dell'host di origine) e quali diritti le servono lì.

## 9. Aggiornare

Aggiorna **prima il gateway, poi gli agent**. Il gateway non invia mai a un agent un comando che non conosce. Le
funzioni che richiedono un agent più recente lo segnalano ("aggiorna l'agent").

**Prima di aggiornare** crea un backup del portale (**Impostazioni > Backup e ripristino**).

### Gateway

- **Dal portale** (dalla 0.9.1): *Impostazioni > Sistema > Aggiornamenti*, **Cerca aggiornamenti**, poi **Installa
  aggiornamento** (servono `settings.manage` e una conferma recente della password). GitHub (`api.github.com`,
  `github.com` e il suo host di download) viene contattato solo quando premi il pulsante, o due volte al giorno se attivi
  il controllo automatico. Il gateway scarica `HvPortal.Gateway.Setup.exe` dell'ultima release, ne confronta lo SHA-256
  con quello pubblicato con la release (digest dell'asset, o la riga nelle note della release) e lo mette in
  `<data>\update`. Poi avvia l'attività pianificata `\HvPortal\HvPortal Gateway Update` (SYSTEM; l'account del
  servizio gateway può solo avviarla). L'attività:
  1. ricontrolla lo SHA-256 e che il setup, il gateway e gli script di installazione al suo interno siano firmati dallo
     **stesso certificato del gateway installato** (un'installazione non firmata non viene mai aggiornata così);
  2. esegue `Install-Gateway.ps1` della nuova release con porte, indirizzo di ascolto, nomi, certificato e cartella dati
     attuali del servizio (copia del database e ripristino automatico come nell'aggiornamento con lo script, sotto);
  3. scrive l'esito in `<data>\update\last-result.json`, mostrato nella stessa pagina.
  Non disponibile in Docker (scarica la nuova immagine) o se il gateway è stato installato con `-DisableRemoteUpdate` o
  prima della 0.9.1: esegui una volta a mano il nuovo setup. Il gateway ha bisogno di HTTPS in uscita verso GitHub solo
  per questo.
- **Setup grafico**: esegui sullo stesso server l'`HvPortal.Gateway.Setup.exe` della nuova versione. Riconosce
  l'installazione, ripropone le impostazioni attuali e copia il database prima di aggiornare. Se la nuova versione non
  parte, quella precedente viene ripristinata da sola.
- **Script**: esegui `Install-Gateway.ps1` della nuova release **con gli stessi parametri usati all'installazione**. Le
  impostazioni del servizio vengono riscritte, quindi un parametro omesso torna al valore predefinito. Lo script copia
  il database in `db\pre-upgrade-<data e ora>\`, salva i binari in `<InstallPath>.bak-<data e ora>` e li rimette a
  posto se il servizio non resta in esecuzione.
- **Docker**: compila la nuova immagine, cambia `HVP_IMAGE` e aggiorna lo stack.

Le migrazioni del database vengono applicate all'avvio del gateway. Per tornare a una release precedente: ferma il
servizio, elimina `hvportal.db-wal` e `hvportal.db-shm`, ripristina la copia `pre-upgrade` e reinstalla la vecchia
release.

### Agent dal portale

Dal portale puoi aggiornare un singolo agent oppure tutti gli agent non aggiornati in una volta. Ogni aggiornamento è
un'attività e richiede il permesso `hosts.manage`. Funziona quando valgono **tutte** queste condizioni:

1. L'agent è già alla **0.7.0 o successiva**. Gli agent precedenti non hanno il meccanismo di aggiornamento: aggiornali
   a mano una volta.
2. L'attività pianificata di aggiornamento è registrata: l'agent è stato installato con `Install-Agent.ps1` 0.7.0 o
   successivo, senza `-DisableRemoteUpdate`.
3. La release è **firmata**: il pacchetto dell'agent del gateway contiene un catalogo firmato, `HvPortal.Agent.cat`.
   Serve `Build-Release.ps1 -SigningCertificateThumbprint`. Le build non firmate, compreso il pacchetto creato dentro
   l'immagine Docker, non vengono mai installate in questo modo.
4. Il pacchetto è firmato con **lo stesso certificato** dell'agent installato. Se firmi una release con un certificato
   diverso, aggiorna gli agent a mano una volta.
5. Il pacchetto non è più vecchio dell'agent installato, e l'agent è connesso.

Che cosa succede sull'host:

1. L'agent scarica il pacchetto dalla porta degli agent del gateway (TLS reciproco, con la stessa identità del suo
   canale). Controlla dimensione e SHA-256 comunicati dal gateway e lo salva in
   `C:\ProgramData\HvPortal\Agent\update\`.
2. L'agent avvia l'attività pianificata. Avviarla è l'unico diritto che l'agent ha su di essa.
3. L'attività, che gira come SYSTEM, copia il pacchetto in una cartella in cui possono scrivere solo SYSTEM e gli
   amministratori, e controlla di nuovo lo SHA-256. Poi richiede:
   - che `HvPortal.Agent.cat` sia firmato con **lo stesso certificato (impronta) dell'`HvPortal.Agent.exe` installato**;
   - che ogni file del pacchetto corrisponda al catalogo, senza file aggiunti (`Test-FileCatalog`);
   - che il nuovo `HvPortal.Agent.exe` sia firmato con quello stesso certificato;
   - una versione non più vecchia di quella installata.
4. Esegue l'`Install-Agent.ps1` contenuto nel pacchetto, che mantiene identità, account di servizio, cartelle di
   storage e dati. Se la nuova versione non parte, vengono ripristinati i binari precedenti.
5. L'esito viene scritto in `C:\ProgramData\HvPortal\Agent\update\last-result.json` e comunicato al gateway quando
   l'agent si ricollega.

L'ancora di fiducia è il certificato che ha firmato l'agent installato a mano, non l'archivio dei certificati di
Windows. Per questo una firma self-signed integra viene accettata anche se la sua radice non è attendibile sull'host.
Né il gateway né il servizio dell'agent possono far installare all'attività codice che quel certificato non ha firmato.
Se le notifiche sono configurate, il gateway segnala anche, una volta al giorno, gli agent non aggiornati.

### Agent a mano

- **Setup grafico**: esegui sull'host il nuovo `HvPortal.Agent.Setup.exe`. Riconosce l'installazione e mantiene il
  collegamento al gateway (senza token), l'account e, se scegli *Mantieni le cartelle impostate ora*, le cartelle di
  storage.
- **Script**: estrai il nuovo pacchetto in una cartella **nuova**, poi eseguilo senza token:

  ```powershell
  Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package-0.7.0
  cd C:\HvPortal\agent-package-0.7.0\installer
  .\Install-Agent.ps1 -Source ..\agent        # aggiungi -DisableRemoteUpdate se non vuoi aggiornamenti dal portale
  ```

  La registrazione in `C:\ProgramData\HvPortal\Agent` viene mantenuta, e se il servizio non parte vengono ripristinati
  i binari precedenti. Per tornare indietro esegui lo stesso comando dalla cartella del pacchetto precedente.

La pagina del portale **Guida > Aggiornare i servizi Windows** mostra i comandi per la tua installazione e la versione
dell'agent di ogni host.

## 10. Disinstallare

Esegui il programma di disinstallazione da PowerShell come amministratore, dalla cartella `installer` della release o
del pacchetto dell'agent (ogni setup la sa estrarre: `HvPortal.Agent.Setup.exe --extract C:\HvPortal\files`).

```powershell
.\Uninstall-HvPortal.ps1 -Component Agent      # su un host; poi rimuovi l'host nel portale
.\Uninstall-HvPortal.ps1 -Component Gateway    # sul server del gateway
```

- La disinstallazione dell'agent rimuove il servizio, la sua appartenenza a *Hyper-V Administrators*, l'attività
  pianificata di aggiornamento, l'utente locale dedicato (solo se l'aveva creato l'installer), i binari e le regole
  firewall di HvPortal.
- La disinstallazione del gateway rimuove il servizio, i binari e le regole firewall.
- Le cartelle dei dati (`C:\ProgramData\HvPortal\Agent`, `C:\ProgramData\HvPortal\Gateway`) vengono **mantenute**, a
  meno che tu non aggiunga `-RemoveData`. Prima di eliminare i dati del gateway crea un backup del portale o esporta le
  chiavi.
- VM, dischi, template, punti di ripristino e cartelle di storage non vengono mai rimossi.
- Docker: rimuovi lo stack. I dati restano nel volume `hvportal-data` finché non lo elimini.

## 11. Risoluzione dei problemi

Log: *Visualizzatore eventi > Registri di Windows > Applicazione*, origini **HvPortal Gateway** e **HvPortal Agent**.
I log dei setup sono in `C:\ProgramData\HvPortal\SetupLogs`. Su un host,
`& 'C:\Program Files\HvPortal\Agent\HvPortal.Agent.exe' status` mostra lo stato dell'agent.

| Sintomo | Che cosa controllare |
|---|---|
| Il setup si ferma con un errore | Le ultime righe nella finestra del setup e il log in `C:\ProgramData\HvPortal\SetupLogs` |
| "La porta 443 è già usata" | IIS o un altro listener HTTP.sys usa la 443 (compare come processo *System*). Scegli un'altra porta o fermalo. |
| L'agent non si collega | Dall'host: `Test-NetConnection <gateway> -Port 8444`. Il nome usato deve essere tra i nomi pubblici del gateway, e l'indirizzo dell'host deve essere ammesso dalla regola firewall del gateway. |
| "L'impronta della CA non corrisponde" | Fermati. Controlla di raggiungere il gateway giusto e confronta l'impronta mostrata nel portale attraverso un canale di cui ti fidi. |
| Registrazione rifiutata | Il token è scaduto (15 minuti) o è già stato usato: creane uno nuovo nel portale. |
| *Accesso Hyper-V* è `AccessDenied` | `Get-LocalGroupMember -SID S-1-5-32-578` deve elencare l'account dell'agent. Riavvia il servizio dell'agent. |
| "Accesso negato" creando VM, caricando file o facendo backup | Permesso NTFS *Modifica* per l'account dell'agent sulla cartella ([5g](#5g-permessi-sulle-cartelle)) |
| Una cartella è "fuori dalle cartelle di storage" | L'host è stato installato con `-StorageFolders`: scegli una cartella dell'elenco, oppure cambia l'elenco sull'host ([5d](#5d-cartelle-di-storage)). |
| `agent_outdated` (501) | L'agent dell'host è più vecchio di quanto richiede la funzione: 0.7.0 per backup, spostamento delle VM, stato di salute dell'host e VLAN delle nuove schede di rete. Aggiorna l'agent ([sezione 9](#9-aggiornare)). |
| Un aggiornamento dal portale viene rifiutato | Il motivo viene mostrato: agent precedente alla 0.7.0, attività pianificata mancante, pacchetto non firmato o firmatario diverso. Vedi la [sezione 9](#9-aggiornare) e `C:\ProgramData\HvPortal\Agent\update\last-result.json`. |
| Un'attività è *Non verificata* o con *Esito sconosciuto* | Hyper-V non ha confermato in tempo lo stato finale, oppure la connessione si è persa dopo l'invio del comando. Controlla la VM prima di riprovare: un'attività non dichiara mai un successo senza prove. |
| Uno spostamento fallisce | Leggi l'errore di Hyper-V nell'attività, poi controlla la [sezione 8](#8-prima-di-spostare-le-vm-tra-host). |
| Nessun amministratore riesce ad accedere | Sul server del gateway (come amministratore): `HvPortal.Gateway.exe admin recover-account --user <nome> --reset-mfa` |

Altri casi sono elencati in [docs/troubleshooting.md](docs/troubleshooting.md) (in inglese).
