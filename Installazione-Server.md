# Installazione HvPortal: gateway + host Hyper-V fuori dominio

Si installa tutto con due file di setup, senza comandi PowerShell:

| File (in `artifacts\release\0.6.0\setup`) | Dove si esegue |
|---|---|
| `HvPortal.Gateway.Setup.exe` | sul server del gateway (il portale web), anche a dominio |
| `HvPortal.Agent.Setup.exe` | sull'host Hyper-V (si può scaricare anche dal portale) |

Entrambi sono procedure guidate in italiano: doppio clic, conferma dei diritti di amministratore, *Avanti*.

> **Avviso di Windows.** I file sono firmati con il certificato NEXQ self-signed, quindi al primo avvio Windows mostra
> "PC protetto da Windows". Scegli *Ulteriori informazioni → Esegui comunque*, oppure importa prima il certificato
> `installer\HvPortal-CodeSigning.cer` (vedi in fondo).

---

## Passo 1 — Installare il gateway

1. Copia `HvPortal.Gateway.Setup.exe` sul server del gateway e aprilo.
2. **Indirizzi e porte**: controlla che tra i nomi ci sia l'indirizzo con cui l'host raggiungerà il gateway. Un host
   fuori dominio di solito non risolve i nomi del dominio: lascia nell'elenco l'**IP** del server.
   Porta del portale: `8443`, oppure `443` per non scrivere la porta nel browser.
3. **Certificato del portale**: lascia *Certificato interno*, oppure scegli *Importa un file di certificato della tua
   CA (.pfx)* e indica file e password.
4. **Windows Firewall**: lascia attiva la creazione delle regole. In *Quali host possono collegarsi come agent* aggiungi
   l'IP dell'host se è in un'altra rete, ad esempio `LocalSubnet, 172.40.1.169`.
5. Premi **Installa**. Alla fine il setup mostra l'indirizzo del portale e il **token di setup**: copialo.

## Passo 2 — Primo accesso al portale

1. Apri l'indirizzo mostrato dal setup (pulsante *Apri il portale*).
2. Inserisci il token, un nome utente amministratore e una passphrase di almeno 15 caratteri.
3. Configura l'app di autenticazione (TOTP) e salva i codici di recupero in un posto sicuro.

## Passo 3 — Installare l'agent sull'host Hyper-V

1. Nel portale: **Aggiungi host → Agent sull'host (consigliato)**. Resta aperta una finestra con il metodo
   **Setup guidato**.
2. Dalla finestra scarica `HvPortal.Agent.Setup.exe` e copialo sull'host.
3. Nella finestra scegli il nome o l'IP del gateway che l'host riesce a raggiungere, poi copia la riga
   **Dati per il setup** (icona di copia). Vale 15 minuti.
4. Sull'host apri il setup:
   - **Dove viene installato l'agent**: *Su un host Hyper-V*.
   - **Collegamento al gateway**: premi **Incolla dal portale**. Se copi da un altro PC, incolla la riga in un file di
     testo sull'host e copiala da lì, oppure scrivi a mano i tre valori.
   - **Account di servizio**: lascia *Account automatico*. Scegli *Utente locale dedicato* solo se le tue regole
     richiedono un utente con nome (il setup lo crea con i permessi minimi).
5. Premi **Installa**. Entro pochi secondi l'host risulta **online** nel portale, con **Accesso Hyper-V = Ok**.

Se vuoi creare VM o caricare file in una cartella dell'host con l'utente dedicato, dagli il permesso di modifica:
```powershell
icacls D:\Hyper-V /grant "svc-hvportal:(OI)(CI)M"
```

### Template (dalla versione 0.6.0)

1. Nel portale apri l'host → **Impostazioni → Libreria template** e indica una cartella su un disco dati dell'host,
   ad esempio `D:\Templates`. L'account dell'agent deve poterci scrivere:
   ```powershell
   icacls D:\Templates /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"   # oppure l'utente dedicato, se ne usi uno
   ```
2. Prepara la VM modello (su Windows esegui Sysprep, su Linux cloud-init o machine-id pulito), **spegnila**, poi dal
   suo menu scegli **Crea template**. I dischi vengono copiati: la VM resta com'è.
3. Per creare una VM: **Template → Nuova VM da questo template** (oppure *Nuova VM → Parti da: Template*), scegli nome,
   CPU, memoria, rete e posizione.

### Variante: niente sull'host (agent di management)

Nel portale: **Inventario → Agent di management → Nuovo agent di management**, poi esegui `HvPortal.Agent.Setup.exe` su
un server Windows (anche quello del gateway) scegliendo *Su un server di management*. La preparazione dell'host
(WinRM, firewall, TrustedHosts) è descritta in [docs/install.md](docs/install.md), sezione 4b.

---

## Aggiornare

Riesegui il setup della nuova versione sullo stesso server: riconosce l'installazione, ripropone le impostazioni
attuali e mantiene il collegamento dell'agent. Prima il gateway, poi gli agent.

Per cambiare porta o certificato del gateway in un secondo momento, riesegui `HvPortal.Gateway.Setup.exe` e modifica
la pagina corrispondente. Se usi l'accesso con Microsoft Entra ID, aggiorna l'URI di reindirizzamento.

## Rendere attendibile il certificato di firma (facoltativo)

Su ogni server, da PowerShell come amministratore, con il file `HvPortal-CodeSigning.cer` della release:
```powershell
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

## Se qualcosa non va

| Sintomo | Cosa controllare |
|---|---|
| Il setup si ferma con un errore | Le ultime righe nel riquadro del setup; il log completo è in `C:\ProgramData\HvPortal\SetupLogs` |
| "La porta 443 è già usata" | Sul server c'è IIS o un altro programma sulla 443: usa un'altra porta o fermalo |
| L'agent non si collega | Dall'host: `Test-NetConnection <IP gateway> -Port 8444`. Il nome usato deve essere tra quelli indicati nel setup del gateway; l'IP dell'host deve essere ammesso dal firewall del gateway |
| "Incolla dal portale" non trova i dati | Copia di nuovo la riga *Dati per il setup*; il token scade dopo 15 minuti (nel portale: *Nuovo token*) |
| Creazione VM o upload "accesso negato" | Permesso di modifica dell'account di servizio sulla cartella (`icacls`) |

Installazione con script PowerShell (per automazioni o Server Core): [docs/install.md](docs/install.md).
