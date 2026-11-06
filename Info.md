# Guida all'avvio di Hyper-V Manager

Questa guida descrive come avviare il gateway in modalità sviluppo, configurare il primo accesso, collegare un host Hyper-V e installare il gateway come servizio Windows.

## 1. Avvio del gateway (modalità sviluppo)

Apri **PowerShell 7** e posizionati nella cartella del progetto:

```powershell
cd "C:\DEV OPS\Hyper-V Manager"
.\scripts\dev\Start-DevGateway.ps1
```

Al primo avvio, lo script:

1. Crea il pacchetto dell'agent nella cartella `artifacts\packages`.
2. Avvia il gateway tramite `dotnet run`.
3. Rende disponibile l'interfaccia web all'indirizzo:

**https://localhost:18443**

> **Nota:** il browser mostrerà un avviso relativo al certificato HTTPS, poiché viene utilizzato un certificato interno. È un comportamento previsto: puoi proseguire.

### Percorso dei dati

I dati dell'ambiente di sviluppo vengono salvati in:

```text
%LOCALAPPDATA%\HvPortal\dev-gateway
```

L'ambiente di sviluppo utilizza porte differenti rispetto alla versione di produzione, evitando conflitti con eventuali installazioni esistenti.

### Ripristino dell'ambiente

Per ripartire da zero, utilizza il parametro `-Reset`:

```powershell
.\scripts\dev\Start-DevGateway.ps1 -Reset
```

> **Importante:** mantieni aperta la finestra PowerShell durante l'utilizzo del gateway. Chiudendo la finestra o premendo `Ctrl+C`, il processo viene arrestato.

---

## 2. Configurazione del primo accesso

Al primo avvio del gateway viene generato automaticamente un **token di setup**.

Per visualizzarlo, esegui:

```powershell
Get-Content "$env:LOCALAPPDATA\HvPortal\dev-gateway\bootstrap\setup-token.txt"
```

> **Stato attuale:** il file non è ancora presente perché il gateway di sviluppo non è mai stato avviato.

### Creazione dell'account amministratore

1. Apri **https://localhost:18443**.
2. Inserisci il token di setup.
3. Scegli un nome utente amministratore.
4. Imposta una passphrase di almeno **15 caratteri**.
5. Configura un'app di autenticazione TOTP.
6. Salva i codici di recupero in un luogo sicuro.

**L'autenticazione TOTP è obbligatoria per tutti gli amministratori.**

---

## 3. Collegamento di un host Hyper-V tramite agent

Il gateway, da solo, mostra un inventario vuoto.

Per visualizzare e gestire macchine virtuali reali è necessario collegare un host con **Hyper-V attivo** e l'**agent installato**.

### 3.1 Aggiunta di un host

Dal portale web:

1. Vai su **Hosts → Add host**.
2. Segui la procedura guidata.
3. Recupera le informazioni fornite dal dialog:
   - Token di registrazione.
   - Impronta digitale della CA.
   - Pacchetto dell'agent da scaricare.
   - Comandi di installazione già compilati.
4. Consulta il menu **Guide** per ulteriori istruzioni.

### 3.2 Collegamento di un host remoto

Se l'host Hyper-V si trova su un'altra macchina, il gateway deve essere accessibile tramite rete.

Avvialo con:

```powershell
.\scripts\dev\Start-DevGateway.ps1 -BindAddress 0.0.0.0 -PublicNames <nome-pc>,<ip-pc>
```

Sostituisci:

- `<nome-pc>` con il nome del computer che ospita il gateway.
- `<ip-pc>` con il relativo indirizzo IP.

È inoltre necessario consentire le connessioni in ingresso sulla **porta 18444** attraverso il firewall.

> **Attenzione:** un agent installato su un altro computer non può collegarsi utilizzando `localhost`, perché questo indirizzo identifica la macchina locale dell'agent e non quella del gateway.

### 3.3 Utilizzo dello stesso PC come host

È possibile utilizzare il computer di sviluppo anche come host Hyper-V, a condizione che:

1. Hyper-V sia installato e abilitato.
2. L'agent venga installato tramite una sessione PowerShell avviata **come amministratore**.
3. L'account utilizzato disponga dei permessi necessari per interagire con Hyper-V.

**Limitazioni attualmente note:**

- Non è stato possibile verificare se Hyper-V sia attivo, poiché il controllo richiede privilegi elevati.
- Il progetto indica che la macchina di sviluppo potrebbe non disporre dei permessi necessari per accedere a Hyper-V.
- In assenza dei permessi richiesti, l'agent può collegarsi correttamente al gateway, ma restituisce l'errore `AccessDenied` durante l'accesso a Hyper-V.

---

## 4. Installazione stabile come servizio Windows

Se desideri mantenere il gateway sempre attivo, senza doverlo avviare manualmente, puoi installarlo come **servizio Windows**.

La documentazione completa è disponibile in:

```text
docs/install.md
```

### 4.1 Creazione del pacchetto di rilascio

Apri PowerShell **come amministratore** ed esegui, dalla cartella principale del progetto:

```powershell
.\installer\Build-Release.ps1
```

### 4.2 Installazione del gateway

Entra nella cartella dell'installer generato:

```powershell
cd artifacts\release\<versione>\installer
```

Avvia l'installazione:

```powershell
.\Install-Gateway.ps1 -Source ..\gateway -PublicNames <nome-pc>,<ip-pc> -OpenFirewall
```

Sostituisci `<versione>`, `<nome-pc>` e `<ip-pc>` con i valori appropriati.

Il parametro `-OpenFirewall` configura l'apertura delle porte necessarie nel firewall Windows.

### 4.3 Accesso al portale

Una volta completata l'installazione, il portale sarà disponibile all'indirizzo:

```text
https://<pc>:8443
```

Il token di setup sarà disponibile nel file:

```text
C:\ProgramData\HvPortal\Gateway\bootstrap\setup-token.txt
```

### 4.4 Alternativa: Docker

Il gateway può essere eseguito anche tramite **container Docker**.

Per le istruzioni di configurazione e avvio, consulta:

```text
docker/README.md
```

---

## 5. Riepilogo delle configurazioni

| Caratteristica | Modalità sviluppo | Installazione stabile |
|---|---|---|
| Avvio | Script PowerShell | Servizio Windows |
| Interfaccia web | `https://localhost:18443` | `https://<pc>:8443` |
| Persistenza del processo | Fino alla chiusura del terminale | Gestita dal servizio Windows |
| Directory dati | `%LOCALAPPDATA%\HvPortal\dev-gateway` | `C:\ProgramData\HvPortal\Gateway` |
| Token di setup | `bootstrap\setup-token.txt` | `bootstrap\setup-token.txt` |
| Accesso remoto | Richiede configurazione esplicita | Configurabile in fase di installazione |
| Apertura firewall | Manuale | Tramite `-OpenFirewall` |
| Gestione VM | Richiede agent Hyper-V | Richiede agent Hyper-V |

## 6. Riferimenti

- **Installazione del gateway:** `docs/install.md`
- **Esecuzione tramite Docker:** `docker/README.md`
- **Script gateway di sviluppo:** `scripts/dev/Start-DevGateway.ps1`
- **Script di build:** `installer/Build-Release.ps1`
- **Script di installazione:** `Install-Gateway.ps1`

