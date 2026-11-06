# Installazione HvPortal: gateway + host Hyper-V fuori dominio

Il **gateway** (il portale web) va su un server Windows, anche a dominio.
Per l'**host Hyper-V** ci sono due varianti:

- **A — Agent sull'host** (consigliata): l'agent si installa sull'host e si collega lui al gateway. Non servono password
  né porte aperte sull'host.
- **B — Agent di management**: sull'host non si installa niente. Un agent su un server Windows raggiunge l'host con
  utente e password salvati nel portale.

Prima di iniziare, sostituisci questi segnaposto ovunque compaiono:

| Segnaposto | Cosa è | Esempio |
|---|---|---|
| `<GATEWAY>` | nome o IP del server del gateway, raggiungibile dall'host | `10.0.10.5` |
| `<HOST>` | nome dell'host Hyper-V | `HV01` |
| `<HOST-IP>` | IP dell'host Hyper-V | `172.40.1.169` |

Ogni comando va eseguito in **PowerShell come amministratore**.

---

## Passo 1 — Installare il gateway (sul server del gateway)

1. Copia la cartella della release (`artifacts\release\0.4.0`) sul server, ad esempio in `C:\HvPortal\0.4.0`.
2. Sblocca i file e rendi attendibile il certificato di firma NEXQ:
   ```powershell
   cd C:\HvPortal\0.4.0
   Get-ChildItem -Recurse | Unblock-File
   Import-Certificate -FilePath .\installer\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
   Import-Certificate -FilePath .\installer\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
   ```
3. Installa il gateway. In `-PublicNames` metti **tutti** i nomi e IP con cui l'host raggiungerà il gateway: un host
   fuori dominio di solito non risolve i nomi del dominio, quindi includi l'IP.
   ```powershell
   cd C:\HvPortal\0.4.0\installer
   .\Install-Gateway.ps1 -Source ..\gateway -PublicNames <GATEWAY>,gw01.contoso.local -OpenFirewall -AgentRemoteAddress <HOST-IP>
   ```
   `-AgentRemoteAddress` serve se l'host è in un'altra rete: per default il firewall accetta gli agent solo dalla stessa
   subnet.

## Passo 2 — Primo accesso al portale

1. Leggi il token di setup:
   ```powershell
   Get-Content C:\ProgramData\HvPortal\Gateway\bootstrap\setup-token.txt
   ```
2. Apri `https://<GATEWAY>:8443`. L'avviso sul certificato è normale: per toglierlo vedi "Porta 443 e certificato della
   tua CA" più sotto.
3. Inserisci il token, un nome utente amministratore e una passphrase di almeno 15 caratteri.
4. Configura l'app di autenticazione (TOTP) e salva i codici di recupero in un posto sicuro.

---

## Variante A — Agent sull'host, con utente di servizio dedicato

### A1. Scaricare il pacchetto

Nel portale: **Aggiungi host → Agent sull'host (consigliato)**. Scarica il pacchetto dalla finestra e copialo sull'host.
La finestra resta aperta: mostra il token (valido 15 minuti), l'impronta della CA e il comando già compilato.

### A2. Preparare l'host

```powershell
Unblock-File .\HvPortal.Agent-win-x64.zip
Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package
cd C:\HvPortal\agent-package\installer
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
Test-NetConnection <GATEWAY> -Port 8444
```

`Test-NetConnection` deve rispondere `TcpTestSucceeded : True`.

### A3. Installare l'agent con l'utente di servizio

Prendi il comando dalla finestra del portale e aggiungi in fondo `-ServiceAccount svc-hvportal`:

```powershell
$env:HVP_ENROLLMENT_TOKEN = '<token>'
.\Install-Agent.ps1 -Source ..\agent -Gateway https://<GATEWAY>:8444 -CaSha256 <impronta> -ServiceAccount svc-hvportal
```

Lo script crea l'utente locale `svc-hvportal` con i minimi permessi necessari:

| Cosa | Impostazione |
|---|---|
| Password | casuale di 40 caratteri, non la conosce nessuno e non scade |
| Gruppi | solo *Hyper-V Administrators*, **non** *Administrators* |
| Diritti | solo "Accesso come servizio" |
| Accessi negati | locale, Desktop remoto, dalla rete, batch |
| Cartelle | programma in sola lettura; dati dell'agent (la sua identità) accessibili solo a lui e agli amministratori |

Se non metti `-ServiceAccount`, l'agent usa l'account virtuale `NT SERVICE\HvPortalAgent`. Ha gli stessi permessi minimi
ma non ha un nome utente.

Se vuoi creare VM o caricare file in una cartella dell'host, dai all'utente il permesso di modifica su quella cartella:
```powershell
icacls D:\Hyper-V /grant "svc-hvportal:(OI)(CI)M"
```

### A4. Verificare

Entro pochi secondi l'host risulta **online** nel portale, con **Accesso Hyper-V = Ok**.

---

## Variante B — Agent di management (niente sull'host)

1. Nel portale: **Inventario → Agent di management → Nuovo agent di management**. Installalo su un server Windows
   (anche quello del gateway) con il comando della finestra, che termina con `-Management`.
2. Sull'host:
   ```powershell
   Enable-PSRemoting -Force -SkipNetworkProfileCheck
   Enable-NetFirewallRule -Group '@FirewallAPI.dll,-34251'   # Strumentazione gestione Windows (WMI)
   Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502'   # Condivisione file e stampanti
   ```
   Solo se non usi l'utente `Administrator` predefinito:
   ```powershell
   New-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force
   ```
3. Sul server dell'agent di management:
   ```powershell
   Set-Item WSMan:\localhost\Client\TrustedHosts -Value '<HOST-IP>' -Concatenate -Force
   Test-WSMan <HOST-IP> -Credential (Get-Credential '<HOST>\Administrator') -Authentication Negotiate
   ```
4. Nel portale: **Aggiungi host → Tramite un agent di management**. Compila così:
   - host: `<HOST-IP>`;
   - utente: `<HOST>\Administrator`;
   - password.

---

## Porta 443 e certificato della tua CA (sul server del gateway)

1. Importa il PFX nell'archivio del computer:
   ```powershell
   $cert = Import-PfxCertificate -FilePath C:\temp\hvportal.pfx -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString 'Password PFX')
   $cert | Format-List Subject, DnsNameList, NotAfter, Thumbprint
   ```
   Il certificato deve contenere il nome che si scrive nel browser, ad esempio `hvportal.contoso.com`.
2. Recupera i parametri attuali del gateway:
   ```powershell
   (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HvPortalGateway).Environment
   ```
3. Rilancia l'installazione con **gli stessi parametri** di prima, più porta e certificato:
   ```powershell
   cd C:\HvPortal\0.4.0\installer
   .\Install-Gateway.ps1 -Source ..\gateway -PublicNames <GATEWAY>,gw01.contoso.local -OpenFirewall -AgentRemoteAddress <HOST-IP> `
       -UiPort 443 -UiCertificateThumbprint $cert.Thumbprint
   ```
   Lo script:
   - si ferma se la porta 443 è già occupata, ad esempio da IIS;
   - dà al servizio il permesso di leggere la chiave privata del certificato;
   - sposta la regola del firewall sulla 443.
4. Apri `https://hvportal.contoso.com`, senza porta. Gli agent continuano a usare la porta 8444: sugli host non cambia
   niente.

## Accesso con Microsoft Entra ID

Nel portale: **Guida → Accesso con Microsoft Entra ID**. La pagina spiega passo passo:
- la registrazione dell'app in Entra, con l'URI di reindirizzamento già pronto da copiare;
- il segreto client;
- le impostazioni del portale;
- la creazione degli utenti Microsoft.

Se hai cambiato porta o nome del portale, l'URI di reindirizzamento in Entra va aggiornato.

---

## Se qualcosa non va

| Sintomo | Cosa controllare |
|---|---|
| L'agent sull'host non si collega | `Test-NetConnection <GATEWAY> -Port 8444`; il nome usato in `-Gateway` deve essere tra i `-PublicNames`; `-AgentRemoteAddress` sul gateway |
| `Install-Agent` dice che un diritto utente non è stato concesso | una Group Policy impone i diritti utente: togli `-ServiceAccount` oppure aggiungi l'utente nella GPO |
| Creazione VM o upload "accesso negato" | permesso di modifica dell'utente di servizio sulla cartella (`icacls`, variante A) |
| `AccessDenied` sull'host (variante B) | password, utente nella forma `HOST\utente`, `LocalAccountTokenFilterPolicy` |
| Il gateway non parte sulla 443 | porta occupata (IIS?), certificato senza chiave privata, nome nel certificato |

Guida completa (in inglese): [docs/install.md](docs/install.md).
