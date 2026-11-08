# Backup e ripristino

> Versione italiana di [backup-restore.md](backup-restore.md). In caso di differenze fa fede la versione inglese.

Questa pagina ha due parti:

- **Parte A — Backup delle VM** (v0.7.0, menu *Backup*): copie delle macchine virtuali, ripristini, test di
  ripristino. Progetto e motivazioni: [ADR-031](decisions.md).
- **Parte B — Lo stato di HvPortal** (database, chiavi, CA): come salvare e recuperare il portale stesso. Un backup di
  VM non contiene il portale, e un backup del portale non contiene nessuna VM.

> **Stato.** I backup delle VM sono implementati e coperti da test automatici che usano un sostituto di prova
> dell'host. Nessun backup, ripristino, montaggio o test di ripristino è ancora stato eseguito su un host Hyper-V reale
> (la macchina di sviluppo non ha diritti Hyper-V). Eseguire le procedure V56–V62 e V67 del
> [piano di test sugli host reali](verification/real-host-test-plan.md) su VM di prova prima di farci affidamento.

---

## Parte A — Backup delle VM

### Come funziona: tecnologia e componenti

HvPortal **non** usa Windows Server Backup (`wbadmin`), né il writer VSS di Hyper-V di un backup a livello di host, né
motori di backup di terze parti. Il backup è fatto con i cmdlet standard del modulo PowerShell di Hyper-V, eseguiti
dall'agent sull'host che ospita la VM, tramite l'unico script fisso delle operazioni `Ops\HvPortal.Ops.ps1` (Windows
PowerShell 5.1, solo operazioni tipizzate, ADR-012). Non serve installare software di backup sugli host o sul NAS.

| Componente | Dove | Cosa fa |
|---|---|---|
| Pianificatore dei backup (`VmBackup/BackupScheduler`) | gateway | ogni 20 s (`Gateway__Backup__IntervalSeconds`): avvia i job in scadenza (anche un avvio perso mentre il gateway era fermo), applica la conservazione dopo ogni esecuzione, accoda i test di ripristino ogni *N* giorni, invia le notifiche |
| Servizio backup ed esecutore dei job (`VmBackup/BackupService`, `Jobs/JobRunner`) | gateway | un'attività persistente per VM (`backup.vm`, `backup.verify`, `backup.restore`, `backup.delete`, ...), autorizzata di nuovo quando parte, al massimo 4 attività lunghe insieme; registra i punti di ripristino e il loro stato nel database del portale |
| Agent | ogni host | riceve il comando tipizzato sul suo canale in uscita ed esegue il ramo corrispondente dello script (`BackupVm`, `VerifyBackup`, `RestoreBackup`, `RestoreBackupDisk`, `MountBackupDisk`, `RemoveBackup`, `ListBackups`, `GetBackupReadiness`) |
| Hyper-V (`vmms`) | ogni host | crea il checkpoint, scrive l'esportazione, importa un punto di ripristino |
| Repository | cartella locale o condivisione SMB / NAS | contiene i punti di ripristino: normali cartelle e file, leggibili anche senza HvPortal |

**Un backup, passo per passo** (`BackupVm`):

1. Si apre il repository. Cartella locale o condivisione di dominio: usata direttamente. Condivisione con credenziali
   salvate (NAS fuori dominio): collegata per la durata dell'attività con `New-PSDrive -Credential`, e l'esportazione
   va in una cartella temporanea `hvportal-<id>` nella **cartella di appoggio** dell'host, perché Hyper-V non può usare
   quelle credenziali.
2. Un checkpoint lasciato da un backup interrotto (`HvPortal backup *`) viene prima rimosso, e il tipo di checkpoint
   che aveva registrato viene rimesso.
3. **Checkpoint** (`Checkpoint-VM`), anche con la VM accesa. Se il tipo di checkpoint del job (Production,
   ProductionOnly, Standard) è diverso da quello della VM, il tipo della VM viene cambiato solo per quel momento e poi
   rimesso.
4. **Esportazione del checkpoint** (`Export-VMSnapshot`): configurazione (`.vmcx`, `.vmgs`, ...) e dischi (`.vhdx`)
   com'erano al momento del checkpoint, scritti da Hyper-V nella cartella del punto di ripristino (o nella cartella di appoggio).
5. **Rimozione del checkpoint** (`Remove-VMSnapshot`, unito da Hyper-V), sempre, anche se l'esportazione fallisce.
6. **SHA-256 di ogni file esportato** (`Get-FileHash`), calcolato sull'host prima di qualsiasi copia.
7. Solo con la cartella di appoggio: **copia** sulla condivisione (`Copy-Item`, file per file) e, con *verifica dopo la copia*, la
   copia viene riletta e confrontata con gli hash; poi la cartella di appoggio viene cancellata.
8. **Manifest** `hvportal-backup.json` scritto per ultimo (VM, host, orari, tipo di checkpoint richiesto e usato, file
   con dimensione e SHA-256, file di configurazione, dischi). Una cartella senza manifest non viene mai usata; un
   backup fallito cancella la cartella che aveva creato.

Chi accede al repository: con una cartella locale o una condivisione di dominio, Hyper-V scrive l'esportazione (su una
condivisione: con l'**account computer** dell'host, `HOST$`) e l'account dell'agent la legge per calcolare gli hash;
con le credenziali salvate solo l'agent legge e scrive sulla condivisione, Hyper-V scrive solo nella cartella di appoggio locale.

**I ripristini** usano gli stessi strumenti:

| Operazione | Cmdlet |
|---|---|
| Come nuova VM / test di ripristino | `Compare-VM -Copy -GenerateNewId` (controllo di compatibilità, non copia niente), `Import-VM -CompatibilityReport` in `<cartella>\<nome>`, `Rename-VM`; test di ripristino: `Start-VM`, attesa dell'heartbeat del guest, poi `Stop-VM -TurnOff`, `Remove-VM` e cancellazione della cartella |
| Sopra la VM originale | lo stesso controllo **prima** di toccare l'originale; poi `Stop-VM -TurnOff`, `Remove-VM`, dischi originali rinominati `.pre-restore-<ora>`, `Import-VM` con l'**id originale** (`Compare-VM -Copy` senza nuovo id) |
| Un disco | `Copy-Item` del `.vhdx` dal punto, facoltativamente `Add-VMHardDiskDrive` (SCSI) |
| Singoli file | `Mount-VHD -ReadOnly` sull'host, volumi trovati tramite WMI; file letti dall'agent a blocchi e inviati dal gateway al browser; `Dismount-VHD` alla fine |
| Verifica | `Get-FileHash` di ogni file, confrontato con il manifest |
| Eliminazione di un punto | rimozione della sola cartella del punto `<cartella VM>\<id punto>` (il nome viene controllato con uno schema fisso) |

Da una condivisione con credenziali salvate i file vengono prima copiati nella cartella di appoggio, perché durante un'importazione
Hyper-V legge l'esportazione da sé.

**Perché non Windows Server Backup.** Lavora a livello di host (volumi o l'intero host Hyper-V, tramite il writer VSS di
Hyper-V) e si comanda host per host con `wbadmin`: non offre dal portale punti di ripristino per singola VM, job,
conservazione, notifiche, permessi per VM o gruppo, test di ripristino o ripristino di file, e su una cartella di rete
tiene solo l'ultimo backup (ogni esecuzione sovrascrive la precedente). L'esportazione di un checkpoint dà un punto nel
tempo di una VM mentre è accesa, in un formato standard di Hyper-V che si può importare con Hyper-V Manager anche senza
HvPortal. Valutato come sviluppo futuro: recovery checkpoint con resilient change tracking (RCT) per copie incrementali
([ADR-031](decisions.md)).

### Cos'è un punto di ripristino, e cosa non è

Un **punto di ripristino** è una copia completa di una VM in un dato momento: l'agent crea un checkpoint, lo esporta
(`Export-VMSnapshot`) nel repository, rimuove di nuovo il checkpoint e scrive un manifest `hvportal-backup.json` con lo
SHA-256 di ogni file esportato. Il manifest è scritto per ultimo: una cartella senza manifest è una copia incompleta e
non viene mai usata. Struttura nel repository:

```
<repository>\<nome VM>_<primi 8 caratteri esadecimali dell'id Hyper-V>\<id punto di ripristino>\
    Virtual Machines\<id>.vmcx ...      (configurazione esportata)
    Virtual Hard Disks\<disco>.vhdx ... (dischi esportati)
    hvportal-backup.json                (manifest, scritto per ultimo)
```

**Coerenza.** Il *tipo di checkpoint* del job decide come viene fatta la copia:

| Tipo | Cosa fa Hyper-V | Cosa si ottiene |
|---|---|---|
| Production (predefinito) | chiede al guest, tramite il servizio di integrazione *Backup*, di scaricare i dati su disco (VSS nei guest Windows, blocco del file system nei guest Linux); se il guest non può partecipare Hyper-V ripiega su un checkpoint standard | il punto di ripristino registra il tipo riportato da Hyper-V (`checkpointTypeUsed`): controllarlo |
| ProductionOnly | come sopra, ma il backup **fallisce** invece di ripiegare | fallisce sui guest senza servizi di integrazione funzionanti |
| Standard | nessun coinvolgimento del guest | dischi coerenti come dopo un'interruzione di corrente (crash-consistent) |

HvPortal **non** verifica né garantisce la coerenza applicativa. Un punto di ripristino è una copia di una VM, non un
backup coerente a livello applicativo dei database o degli archivi di posta al suo interno: proteggerli con i loro
strumenti (dump nativi, backup application-aware) e provare i ripristini.

**Non previsto:** copie incrementali o differenziali (ogni punto è una copia completa), deduplica, compressione,
cifratura del repository, immutabilità (WORM), una seconda copia dei punti, backup di VM con i checkpoint
disabilitati, ricerca di file, ripristino di singoli elementi applicativi, gestione dei cluster di failover.

### Prima di iniziare

1. **Agent 0.7.0** sugli host (i backup richiedono il protocollo agent 9; gli agent più vecchi vengono rifiutati con
   "aggiorna l'agent").
2. **Permessi** (Utenti e ruoli): `backup.manage` (Globale) per creare repository e job e per eliminare punti di
   ripristino; `backup.run` per fare il backup delle VM ed eseguire i job; `backup.restore` per ripristinare. Il ruolo
   predefinito *Backup operator* ha tutti e tre più `inventory.read`, `jobs.read.all` e `vm.create` (un ripristino come
   nuova VM crea una VM). La pagina Backup richiede uno dei tre permessi con ambito **Globale**; chi li ha su singole VM
   usa la scheda *Backup* della VM.
3. **Checkpoint abilitati** su ogni VM da proteggere (impostazioni VM > tipo di checkpoint diverso da *Disabled*),
   servizi di integrazione installati e servizio di integrazione *Backup (copia shadow del volume)* abilitato per i
   checkpoint Production.
4. **Un repository** (sezione successiva) e, dove serve, una **cartella di appoggio dei backup** per host (Host >
   Impostazioni > Cartella di appoggio dei backup, `hosts.manage` o `backup.manage`): una cartella locale, non la radice di
   un disco, abbastanza grande per una copia completa della VM più grande di quell'host. È obbligatoria per le
   condivisioni con credenziali salvate, ed è usata per i test di ripristino e per il ripristino di file da quelle
   condivisioni.
5. **Cartelle storage consentite** ([ADR-034](decisions.md)): se un host limita le sue cartelle, la cartella del
   repository, quella di appoggio e quelle di ripristino devono stare al loro interno.
6. **Controllo**: Backup > Repository > *Controlla* (o la prontezza ai backup dell'host) elenca cosa manca su un host,
   con la correzione: modulo e cmdlet Hyper-V, impostazione dei checkpoint e servizio di integrazione Backup di ogni VM,
   prova di scrittura e spazio libero del repository e della cartella di appoggio.

### Repository

| Tipo | Percorso | Chi scrive | Note |
|---|---|---|---|
| Cartella locale di un host | `D:\Backups` | Hyper-V (esportazione) e l'account dell'agent (cartelle, hash, cancellazioni) | solo le VM di quell'host possono usarlo; dare all'account dell'agent *Modifica* sulla cartella se non è creata dall'agent; stesso disco delle VM = nessuna protezione dalla perdita dell'host |
| Condivisione SMB nel dominio, senza credenziali | `\\fs01\hvbackup` | l'**account computer** di ogni host (`H1$`, `H2$`) — previsto, non ancora verificato (piano host reali V67) | dare diritti di condivisione e NTFS agli account computer di tutti gli host che fanno backup o ripristini |
| Condivisione SMB fuori dominio (NAS in un workgroup) | `\\nas01\backups` + utente (`NAS01\backup`) e password | l'agent, con le credenziali salvate, dalla **cartella di appoggio** dell'host | Hyper-V non può usare quelle credenziali: l'esportazione viene preparata nella cartella di appoggio dell'host e poi copiata (e, con *verifica dopo la copia*, riletta e confrontata); serve spazio per una VM completa nella cartella di appoggio |

- La password è cifrata con il key ring del gateway, non viene più mostrata, non viene mai scritta nel log di audit né
  nei parametri dei job; viene inviata all'host solo dentro il comando dell'operazione che la usa. Usare un account del
  NAS dedicato con diritti solo sulla condivisione dei backup.
- Il percorso di un repository non si può cambiare (i suoi punti di ripristino sono lì). Un repository si può eliminare
  solo quando nessun job lo usa e non contiene punti di ripristino.
- Le condivisioni nascoste o amministrative (`\\server\D$`) vengono rifiutate.

### Job di backup

Backup > Job di backup > *Nuovo job di backup*:

- **Cosa**: VM e/o gruppi. Un gruppo comprende i suoi sottogruppi e viene risolto a ogni esecuzione, quindi le VM
  aggiunte dopo sono protette. Serve `backup.run` su tutto ciò che si seleziona.
- **Dove**: un repository.
- **Quando**: *giornaliero* (giorni della settimana e uno o più orari `HH:mm` in un fuso orario; un orario saltato dal
  cambio dell'ora legale viene eseguito un'ora dopo), *orario* (ogni 1–24 ore) o *manuale*.
- **Conservazione**: gli ultimi *N* punti di ripristino validi per VM (1–366) e, facoltativamente, nessuno più vecchio
  di *D* giorni. I punti segnati *Conserva* e l'ultimo punto valido di ogni VM non vengono mai rimossi; i punti falliti
  e non verificati più vecchi dell'ultimo valido vengono rimossi. Anche la cancellazione dei file è un'attività, su un
  host che raggiunge il repository.
- **Tipo di checkpoint**, **verifica dopo la copia** (solo con la cartella di appoggio: rilegge la copia dalla condivisione e confronta lo
  SHA-256), **test di ripristino ogni N giorni** (vedi Test di ripristino), **notifica** (mai / errore / avviso /
  sempre).

Come si comportano le esecuzioni:

- Un'esecuzione crea un punto di ripristino e un'attività (job `backup.vm`) per VM. Le attività girano **come l'utente
  che ha salvato il job per ultimo** e vengono autorizzate di nuovo per ogni VM quando partono: una VM di cui
  quell'utente non può più fare il backup fallisce con *autorizzazione revocata* (salvare il job con un account che ha
  `backup.run` su tutto).
- Un avvio perso mentre il gateway era fermo viene eseguito una volta al suo ritorno. Un avvio viene saltato se
  l'esecuzione precedente dello stesso job è ancora in corso.
- Al massimo 4 attività lunghe (backup, verifiche, ripristini, spostamenti, aggiornamenti agent) girano insieme
  (`Gateway__Jobs__MaxConcurrentLongJobs`), separate dalle attività di accensione e configurazione. Un backup può
  durare ore (fino a 12 h); mentre una VM è in backup, le altre attività su quella VM aspettano.
- Durante un backup la VM ha un checkpoint chiamato `HvPortal backup <id>`, che viene rimosso (unito) dopo
  l'esportazione. Se il tipo di checkpoint della VM è diverso da quello del job, viene cambiato per il momento del
  checkpoint e poi rimesso.

**Esegui backup ora** (menu della VM o scheda *Backup* della VM, `backup.run` sulla VM): un punto di ripristino in un
repository utilizzabile dall'host della VM. Questi punti non hanno conservazione automatica: vanno cancellati a mano.
Anche i punti di un job eliminato restano, senza conservazione.

### Controllare i risultati

- **Panoramica**: esecuzioni delle ultime 24 h, VM protette e non protette (non in un job abilitato e senza un punto
  valido negli ultimi 7 giorni), ultimi problemi.
- **Esecuzioni**: *Riuscita*, *Con avvisi* (qualche VM fallita o non verificata) o *Non riuscita* (nessun punto valido), con il
  messaggio di ogni VM fallita.
- **Punti di ripristino**: una riga per punto.

| Stato | Significato | Cosa fare |
|---|---|---|
| In corso | l'attività di backup è in corso | aspettare |
| OK | l'agent ha restituito il manifest di questo punto; SHA-256 registrati al momento del backup | utilizzabile |
| Non verificato | il risultato dell'attività è andato perso, o il punto è stato trovato da una nuova scansione | eseguire *Verifica* prima di farci affidamento |
| Non riuscito | il backup non è terminato; nessuna copia utilizzabile | leggere l'errore |
| In eliminazione / Eliminato | cancellazione in corso / fatta | — |

- **Notifiche** (Impostazioni > Notifiche, [ADR-033](decisions.md)): e-mail (SMTP con STARTTLS) e webhook (JSON
  generico, Microsoft Teams, Slack) alla fine delle esecuzioni secondo il job, per i test di ripristino falliti, e ogni
  giorno per gli agent non aggiornati. Usare *Invia una prova*.
- **Sistemi di monitoraggio** (Impostazioni > Token API, [ADR-032](decisions.md)): un token di sola lettura per
  `GET /api/v1/public/backup/status` (stato complessivo `ok` / `warning` / `critical`, job, VM e il loro ultimo punto)
  e `/public/backup/runs`. Il token viene mostrato una sola volta; non è legato all'utente che l'ha creato — revocarlo
  quando non serve più.

### Verifica

- **Verifica (SHA-256)** (menu del punto di ripristino, `backup.run`): rilegge ogni file del punto e confronta dimensione e
  SHA-256 con il manifest. Trova copie danneggiate o incomplete. Non può rilevare una modifica intenzionale fatta
  insieme al manifest da chi può scrivere sulla condivisione: proteggere la condivisione.
- **Test di ripristino** (menu del punto di ripristino, o *test di ripristino ogni N giorni* nel job): il punto viene
  ripristinato come nuova VM `hvp-test-<id>` con le schede di rete **scollegate**, avviato, e il test passa quando
  l'heartbeat del guest risponde OK entro 10 minuti; poi la VM viene spenta, rimossa e la sua cartella cancellata. Sugli
  host con una cartella di appoggio la VM di prova viene creata in `<cartella di appoggio>\restore-tests`; i test
  pianificati saltano gli host senza cartella di appoggio. Un test superato dimostra che la VM si avvia e che i servizi di
  integrazione funzionano, non che le applicazioni funzionano.

### Ripristino

Menu del punto di ripristino > *Ripristina la VM…* (`backup.restore`). Il ripristino gira su un host che può usare il
repository.

| Modalità | Cosa succede | Serve | Attenzione |
|---|---|---|---|
| **Come nuova VM** | il punto viene importato come copia con un **nuovo id** e il nome scelto, in `<cartella>\<nome>` (o nelle cartelle predefinite dell'host); schede di rete come nel punto, scollegate, o tutte su un solo switch; avvio facoltativo | `vm.create` sull'host di destinazione; un nome VM libero; spazio sufficiente | la VM originale non viene toccata; la nuova VM non è in nessun gruppo (posizionarla e assegnare i ruoli); due copie con la stessa identità del guest sulla stessa rete vanno in conflitto — avviare scollegata se l'originale è ancora accesa |
| **Sopra la VM originale** | solo sulla VM da cui è stato preso il punto, senza checkpoint; digitarne il nome. La VM viene spenta e **rimossa da Hyper-V**; i suoi file disco vengono rinominati `<file>.pre-restore-<yyyyMMddHHmmss>` (tenuti, non cancellati); il punto viene importato con id, nome e cartelle originali | nome digitato | lo stato attuale viene scartato; i dischi rinominati continuano a occupare spazio (cancellarli quando il ripristino è confermato); **se l'importazione fallisce, i dischi riprendono il loro nome ma la VM resta rimossa da Hyper-V**: ricrearla dai suoi dischi o ripristinarla come nuova VM |
| **Test di ripristino** | vedi Verifica | `vm.create` sull'host | — |
| **Un disco** | un disco del punto viene copiato in una cartella dell'host come `<disco>-restored-<ora>.vhdx` e facoltativamente collegato a una VM (SCSI; una VM di generazione 2 accesa lo accetta) | `vm.config.storage` su quella VM, o `host.files.write` sull'host | per ricopiare file con gli account del guest: collegare, copiare dentro il guest, scollegare e cancellare il disco |
| **Singoli file** | Backup > Ripristino file, o *Sfoglia i file…* su un punto: un disco viene collegato **in sola lettura** a un host che ha un proprio agent; i suoi volumi vengono elencati; i file si scaricano dal browser | host con un proprio agent (non host remoti) | solo i volumi che ricevono una lettera di unità (volumi BitLocker e file system sconosciuti non si leggono); i permessi dei file del guest valgono per l'account dell'agent, quindi le cartelle protette (profili utente) vengono rifiutate — usare *Ripristina un disco…*; solo chi ha collegato un disco può sfogliarlo; viene scollegato alla chiusura, dopo 2 h di inattività o al riavvio dell'agent; ogni download viene registrato nell'audit con il suo SHA-256 |

Le incompatibilità di rete trovate da Hyper-V durante un'importazione (uno switch che non esiste sull'host di
destinazione) scollegano la scheda con un avviso; qualsiasi altra incompatibilità ferma il ripristino. Una VM con TPM
virtuale potrebbe non avviarsi su un altro host senza il suo protettore di chiave (da verificare, V57).

### Dopo una reinstallazione del portale o un ripristino del database: nuova scansione

Backup > Repository > *Nuova scansione* legge i manifest nel repository e aggiunge come **Non verificati** i punti di
ripristino che il portale non conosce. Verificarli, poi ripristinare come al solito. I punti di ripristino la cui VM
non esiste più vengono mostrati solo a chi ha permessi di backup con ambito Globale.

### Spazio e tempi

Ogni punto di ripristino è una copia completa dei dischi e della configurazione della VM: spazio del repository ≈
dimensione dei file della VM × punti tenuti per VM, più una copia completa nella cartella di appoggio mentre si usa una
condivisione con credenziali. I backup leggono l'intera VM; le copie verso un NAS passano in rete alla velocità della
condivisione. Pianificare le VM grandi fuori dall'orario di lavoro e annotare la durata delle prime esecuzioni.

### Messaggi frequenti

| Messaggio / codice | Causa | Soluzione |
|---|---|---|
| `staging_required` | il repository usa credenziali e l'host non ha una cartella di appoggio | Host > Impostazioni > Cartella di appoggio dei backup |
| `repository_other_host` | un repository a cartella locale di un altro host | usare una condivisione, o un repository di questo host |
| "Checkpoints are disabled for this VM" | tipo di checkpoint *Disabled* | impostare Production (impostazioni VM) |
| "cannot be reached with the computer account of this host" | condivisione senza credenziali, mancano i diritti per `HOST$` | dare i diritti all'account computer, o usare credenziali + cartella di appoggio |
| `repository_credentials_unreadable` | il key ring che ha cifrato la password non è disponibile (portale spostato senza le sue chiavi) | inserire di nuovo la password |
| `backup_in_progress` | un backup di quella VM è già in corso | aspettare |
| `agent_outdated` | agent precedente alla 0.7.0 | aggiornare l'agent |

---

## Parte B — Lo stato di HvPortal

Questa parte riguarda il backup dello **stato di HvPortal**. I punti di ripristino delle VM non ne fanno parte; il
database del portale contiene però i record di repository (con le password cifrate), job e punti di ripristino, quindi
dopo un recupero i punti di ripristino sono di nuovo noti (o si ritrovano con *Nuova scansione*).

### Dal portale (consigliato): Impostazioni > Backup e ripristino

- **Crea backup** (amministratore della piattaforma, nuova autenticazione): scegliere una passphrase (≥ 20 caratteri)
  e scaricare un file `.hvpbak`. Contiene una copia coerente del database (backup online SQLite, il servizio resta
  attivo), il key ring e la CA interna, sigillati con la passphrase (stesso formato di `admin export-keys`). Registrato
  nell'audit con lo SHA-256 del file. Conservare file e passphrase separatamente, offline.
- **Ripristino**: scegliere il file, inserire la passphrase, **Verifica** (mostra data, versione, gateway e contenuto;
  non scrive niente), digitare `RIPRISTINA`, **Prepara ripristino**. Il ripristino viene preparato (`restore-pending\`,
  key ring protetto di nuovo con DPAPI su questa macchina) e applicato al successivo avvio del servizio, prima di
  aprire il database; database, key ring e file della CA precedenti vengono tenuti in `db\pre-restore-<timestamp>\`.
  Dopo l'avvio tutte le sessioni vengono revocate e `settings.backup.restore.applied` viene scritto nel log di audit
  (ripristinato). **Riavvia ora il servizio** termina il processo così che il ripristino automatico del servizio lo
  riavvii (installato da `Install-Gateway.ps1`).
- Un backup fatto da un HvPortal più recente (migrazione del database sconosciuta) viene rifiutato: installare prima
  quella versione.
- Gli host registrati dopo il backup vanno registrati di nuovo; i dati delle VM non fanno parte di questo backup.

### Cosa salvare (procedura manuale)

| Elemento | Percorso (predefinito `C:\ProgramData\HvPortal\Gateway`) | Note |
|---|---|---|
| Database | `db\hvportal.db` (+ `-wal`, `-shm`) | utenti, ruoli, host, inventario, job, audit, record di repository/job/punti di ripristino dei backup |
| Key ring + CA interna | tramite `admin export-keys` | **necessario** per leggere i segreti TOTP, le password di host remoti, repository e SMTP, mantenere la fiducia degli agent, verificare l'audit |
| Configurazione | ambiente del servizio (registro) o `appsettings.Production.json` se usato | porte, nomi, impronta del certificato |
| Certificato dell'interfaccia | la vostra PKI | se si usa un certificato personalizzato |
| Pacchetto agent | `<gateway>\packages` | ricreato dalla release; serve per aggiornare gli agent dal portale |

Il solo database non basta: segreti TOTP, password salvate e chiave privata della CA sono cifrati con chiavi **fuori**
dal database. Il file `keys\master.json` protetto con DPAPI non si può usare su un'altra macchina — per questo esiste
l'esportazione con passphrase.

### Esportazione delle chiavi (dopo l'installazione, e dopo ogni `admin rotate-keys`)

```powershell
& 'C:\Program Files\HvPortal\Gateway\HvPortal.Gateway.exe' admin export-keys --out D:\secure\hvportal-keys.hvpx
```
Passphrase ≥ 20 caratteri (o `HVP_EXPORT_PASSPHRASE`). Conservare file e passphrase separatamente, offline, con una
procedura a due persone. L'esportazione contiene tutte le chiavi mai create più `pki\ca.bin` e `pki\server.bin`.

### Copie automatiche del database (ADR-023)

Il gateway copia da solo il suo database: una volta al giorno, verificato con `PRAGMA integrity_check`, tenuto per 7
giorni in `<data>\db\backups\hvportal-auto-<data UTC>-<ora>.db` (Impostazioni > Backup e ripristino le mostra e offre
*Copia ora*). Configurazione: `Gateway:Database:AutoBackupEnabled` (predefinito true), `AutoBackupIntervalHours` (24),
`AutoBackupRetentionDays` (7). Nel servizio Windows usare le variabili d'ambiente
`Gateway__Database__AutoBackupRetentionDays`, ecc.

Queste copie proteggono solo da un **file di database danneggiato**: sono sullo stesso disco e non contengono il key
ring. Contro la perdita del server continuare a scaricare il backup sigillato (sopra) e conservarlo altrove.

Tornare a una copia (database danneggiato, gateway che non parte o segnala errori del database):
```powershell
Stop-Service HvPortalGateway
cd C:\ProgramData\HvPortal\Gateway\db
Rename-Item hvportal.db hvportal.db.damaged
Remove-Item hvportal.db-wal, hvportal.db-shm -ErrorAction SilentlyContinue
Copy-Item .\backups\hvportal-auto-<date>-<time>.db .\hvportal.db
Start-Service HvPortalGateway
```
Tutto ciò che è cambiato nel portale dopo quella copia va perso (utenti, ruoli, gruppi, job, voci di audit);
l'inventario viene ricostruito dagli agent alla loro prossima comunicazione. La copia deve venire dallo stesso gateway
(stesso key ring). I punti di ripristino creati dopo la copia sono ancora nei loro repository: usare *Nuova scansione*
(Parte A).

### Backup del database

Copia coerente (consigliata): fermare il servizio, copiare `db\*`, avviare il servizio.
```powershell
Stop-Service HvPortalGateway; Copy-Item C:\ProgramData\HvPortal\Gateway\db\* D:\backup\hvportal\ ; Start-Service HvPortalGateway
```
Alternativa online: backup online SQLite (`sqlite3 hvportal.db ".backup D:\backup\hvportal.db"`) se lo strumento
sqlite3 è approvato nel vostro ambiente. Non copiare il file del database mentre il servizio è attivo senza l'API di
backup online (modalità WAL).

### Ripristino / disaster recovery su un nuovo server

1. Installare il gateway con la stessa versione (`Install-Gateway.ps1`), poi `Stop-Service HvPortalGateway`.
2. `HvPortal.Gateway.exe admin import-keys --in hvportal-keys.hvpx --force` (con privilegi elevati).
3. Copiare i file del database in `db\`.
4. Usare gli stessi `-PublicNames` (gli agent controllano il nome del gateway con la CA ripristinata) e avviare il
   servizio.
5. Verificare: accesso con TOTP, *Audit → Verifica integrità*, host che si ricollegano, Backup > Repository >
   *Controlla*.
Se l'esportazione delle chiavi è persa: gli agent vanno registrati di nuovo, gli utenti devono riconfigurare l'MFA (gli
amministratori possono azzerarla), la verifica della catena di audit non è più possibile per le voci vecchie, e le
password di host remoti, repository di backup e SMTP vanno inserite di nuovo.

### Rotazione delle chiavi

`admin rotate-keys` aggiunge una nuova chiave attiva; i dati cifrati esistenti restano leggibili con le chiavi più
vecchie. Esportare di nuovo dopo la rotazione. La ricifratura dei segreti esistenti con la nuova chiave è un'attività
della Milestone 2.

### Retention dell'audit / copia esterna

`GET /api/v1/audit/export` (JSON Lines, include gli hash) — pianificare un'esportazione verso un sistema esterno in sola
aggiunta. La catena interna al prodotto rileva le manomissioni ma non è immutabile.
