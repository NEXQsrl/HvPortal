<p align="center"><a href="SECURITY.md">English</a> | <b>Italiano</b></p>

# Politica di sicurezza

## Segnalare una vulnerabilità

Segnala le vulnerabilità **in forma riservata**. Non aprire una issue pubblica, non pubblicare dettagli prima che sia
disponibile una correzione e non fare prove su sistemi che non sono tuoi o che non sei autorizzato a provare.

**Come segnalare**: usa la segnalazione privata delle vulnerabilità di GitHub per questo repository: apri la
[scheda Security](https://github.com/NEXQsrl/HvPortal/security) e scegli **Report a vulnerability**
([link diretto](https://github.com/NEXQsrl/HvPortal/security/advisories/new)). La segnalazione è visibile solo ai
manutentori finché non viene pubblicato un avviso. Se non puoi usare GitHub, contatta NEXQ tramite
[www.nexq.it](https://www.nexq.it) e chiedi un canale riservato, senza inserire i dettagli nel primo messaggio.

Nella segnalazione indica:

- la versione o il commit interessati e come è installato HvPortal (servizio Windows o container; versioni di gateway
  e agent; host in dominio o in workgroup);
- i passaggi per riprodurre il problema e l'impatto osservato o previsto;
- se credenziali, token, chiavi o dati di audit possono essere esposti o alterati.

Lascia ai manutentori un tempo ragionevole per analizzare il problema e pubblicare una correzione prima di qualsiasi
divulgazione pubblica.

### Ambito

**Rientrano nell'ambito**: gateway, agent, installer e setup grafici, interfaccia web, API REST, protocollo di
registrazione e canale degli agent, meccanismo di aggiornamento dell'agent, crittografia e gestione delle chiavi,
logica di autorizzazione.

**Non rientrano nell'ambito**:

- le vulnerabilità di Windows, di Hyper-V o delle dipendenze di terze parti (segnalale a chi le produce, e avvisaci se
  HvPortal ne è colpito);
- i denial of service che richiedono di inondare la rete di traffico;
- i problemi che richiedono diritti di amministratore locale sul server del gateway o su un host. Un amministratore di
  quelle macchine è considerato attendibile per progetto: può leggere le chiavi e controllare direttamente Hyper-V
  (vedi il [modello delle minacce](docs/threat-model.md), in inglese).

## Versioni supportate

HvPortal è un'anteprima, non ancora alla versione 1.0. Le correzioni di sicurezza vengono fatte su `main` e
distribuite con la release successiva; le riceve solo l'ultima release.

| Versione | Correzioni di sicurezza |
|---|---|
| 0.7.x (ultima anteprima) | Sì |
| Precedenti alla 0.7.0 | No: aggiorna all'ultima release |

HvPortal **non ha ancora avuto una revisione di sicurezza indipendente**. Non esporre su Internet né il portale né la
porta degli agent. Limita entrambe le porte alle reti che ne hanno bisogno (`-UiRemoteAddress`, `-AgentRemoteAddress`
o il tuo firewall).

## Il modello di sicurezza in breve

La descrizione completa è in [docs/security-model.md](docs/security-model.md) e
[docs/threat-model.md](docs/threat-model.md) (in inglese).

### Privilegio minimo

- Il **gateway** gira con l'account virtuale `NT SERVICE\HvPortalGateway`, che non è amministratore. I suoi binari sono
  per lui in sola lettura e la sua cartella dei dati è privata (SYSTEM, Administrators e il servizio). In un container
  gira con un utente senza root, con il file system radice in sola lettura, senza capability Linux e con
  `no-new-privileges`.
- L'**agent** gira come `NT SERVICE\HvPortalAgent`, membro del solo gruppo locale *Hyper-V Administrators*; non è
  amministratore locale. Se serve un account con nome, l'installer crea un utente locale dedicato che ha solo
  *Accesso come servizio*: gli accessi locale, tramite Desktop remoto, dalla rete e come processo batch sono negati, e
  la sua password casuale non la conosce nessuno.
- Nessun componente richiede un account di dominio o diritti di Domain Admin.
- Il gateway **non possiede credenziali degli host** che hanno un agent. Esistono credenziali solo per gli host che
  decidi di raggiungere tramite un agent di management, e sono conservate cifrate.

### Canale degli agent

- È l'agent ad **aprire** la connessione: un WebSocket in uscita su TLS reciproco (TLS 1.2 o 1.3). Gli host non hanno
  bisogno di porte in ingresso.
- Il gateway gestisce una **CA interna**. Un host si registra con un token monouso (valido 15 minuti) e con l'impronta
  SHA-256 di quella CA, che l'agent fissa. Da quel momento l'agent considera attendibile solo quella CA per il gateway
  (l'archivio dei certificati di Windows viene ignorato) e controlla il nome del gateway.
- I certificati degli agent valgono 90 giorni e si rinnovano da soli. A ogni connessione vengono confrontati con il
  database, e si possono revocare dal portale.

### Un insieme di comandi chiuso e tipizzato

- Il gateway può inviare a un agent solo comandi di un **elenco chiuso di operazioni tipizzate**, con argomenti
  tipizzati. Non esiste un comando che esegua uno script, una query WMI o un percorso forniti da chi chiama.
- Le operazioni di gestione passano da **un unico script PowerShell fisso**, che riceve la richiesta in JSON sullo
  standard input e ha nomi di parametri fissi. La suite di test fallisce se quello script contiene `Invoke-Expression`,
  `Start-Process`, `Add-Type`, `[ScriptBlock]::Create` o costrutti simili.
- Gli argomenti vengono validati quando la modifica viene richiesta, di nuovo quando l'attività viene eseguita e di
  nuovo dall'agent. I valori che controlla il server (l'id Hyper-V di una VM, le cartelle delle librerie, i percorsi dei
  template, la VLAN di una rete, le credenziali dei repository) li inserisce il gateway dai propri dati, senza mai
  prenderli dal client.
- I percorsi dei file vengono normalizzati e controllati: cartelle di sistema, `..` e reparse point vengono rifiutati.
  Con `-StorageFolders` è l'agent stesso a rifiutare ogni cartella fuori dall'elenco scelto sull'host; il gateway non
  può modificare quell'elenco.

### Autenticazione e sessioni

- Account locali: PBKDF2-HMAC-SHA256 con 600.000 iterazioni; passphrase da 15 a 256 caratteri confrontate con un
  elenco di password comuni; ritardi progressivi e limiti di frequenza al posto dei blocchi.
- Verifica in due passaggi con TOTP, obbligatoria per impostazione predefinita per chi ha permessi amministrativi. Ha
  la protezione contro il riutilizzo dei codici e codici di recupero monouso.
- Accesso con Microsoft Entra ID (authorization code con PKCE) per gli utenti creati prima nel portale.
- Le sessioni sono conservate sul server, con un timeout di inattività e una durata massima. Le azioni critiche
  richiedono una nuova autenticazione recente.
- L'interfaccia web usa solo HTTPS, una Content Security Policy senza script inline, token anti-falsificazione, il
  controllo dell'Origin e cookie `SameSite=Strict`. La console delle VM si apre con un ticket monouso legato
  all'utente, alla sessione e alla VM.

### Autorizzazione

- **Negato per impostazione predefinita.** I ruoli si assegnano a livello globale, di host, di gruppo o di VM, e un
  divieto esplicito prevale su qualsiasi consenso.
- Le VM che un utente non può vedere restituiscono 404, esattamente come le VM che non esistono. I rifiuti vengono
  registrati nell'audit.
- **Contro l'escalation dei privilegi**: nessuno può assegnare ruoli a sé stesso o concedere permessi che non ha, e lo
  spostamento di VM tra gruppi conta come una modifica degli accessi.
- I permessi vengono controllati quando una modifica viene richiesta e di nuovo quando la sua attività viene eseguita.
  I limiti di un gruppo (quote, host, reti, percorsi di archiviazione) valgono in entrambi i momenti.

### Audit

- Ogni modifica e ogni tentativo rifiutato vengono registrati: chi, come ha fatto l'accesso, che cosa, da dove e con
  quale esito.
- Ogni voce è concatenata alla precedente con HMAC-SHA256, quindi le modifiche alle voci salvate si possono rilevare.
  La catena si può verificare (*Audit > Verifica integrità*, oppure `HvPortal.Gateway.exe admin verify-audit`) ed
  esportare in formato JSON Lines.
- La catena rende visibili le manomissioni ma non è immutabile: chi possiede sia il database sia il key ring potrebbe
  ricostruirla. Esporta regolarmente il registro verso un sistema esterno in sola aggiunta.

### Segreti

- Un key ring di chiavi AES-256 protegge i segreti. Su Windows è protetto da DPAPI (a livello di macchina); in un
  container, da una chiave di cifratura delle chiavi fornita da te.
- **Cifrati con il key ring**: i segreti TOTP, la chiave privata della CA interna, il segreto client di Entra ID, la
  password SMTP, le password dei repository di backup e degli host raggiunti tramite un agent di management. Nell'API
  questi valori sono in sola scrittura: non vengono mai restituiti.
- **Conservati nel database solo come hash**: i token API (SHA-256), i codici di recupero dell'MFA e i token di setup,
  di registrazione e di recupero dell'account. I token vengono mostrati una sola volta, quando vengono creati. Fino al
  primo setup il token di setup viene scritto anche in un file che possono leggere solo gli amministratori.
- Le credenziali di un repository di backup vengono decifrate solo per inviarle all'agent, sul canale a TLS reciproco,
  per l'operazione in corso. Non finiscono mai nei parametri delle attività e non vengono mai scritte nei log.
- Gli indirizzi dei webhook sono conservati così come vengono inseriti, perché spesso contengono un segreto. Il
  portale li mostra solo mascherati.
- Password, token e credenziali non vengono mai scritti nei log né nei dettagli dell'audit. HvPortal non contiene
  telemetria.

### Token API

- I token API sono **di sola lettura** e limitati a un solo ambito, `backup.read`. Aprono solo gli endpoint sotto
  `/api/v1/public/` (stato dei backup ed esecuzioni recenti), mai il resto dell'API.
- Per crearne uno servono `settings.manage` e un'autenticazione recente. I token possono scadere e si possono revocare.
  Il loro uso ha un limite di frequenza, e sia le creazioni sia gli usi rifiutati vengono registrati nell'audit.

### Firma del codice e aggiornamento degli agent

- Le release si possono firmare con Authenticode. Le build del progetto usano un certificato **self-signed**. Una firma
  self-signed dimostra che i file non sono cambiati da quando li ha firmati chi possiede quel certificato. Non rende
  attendibile una build e non dà alcuna reputazione presso SmartScreen o gli antivirus. Windows la segnala come non
  attendibile finché non importi il certificato pubblico (`HvPortal-CodeSigning.cer`). Non considerare mai affidabile
  una build solo perché è firmata: verifica lo SHA-256 e la provenienza.
- Gli aggiornamenti degli agent dal portale vengono installati solo se il catalogo firmato del pacchetto
  (`HvPortal.Agent.cat`) e il nuovo eseguibile sono firmati con **lo stesso certificato dell'agent già installato**,
  ogni file corrisponde al catalogo e la versione non è più vecchia. Le build non firmate non vengono mai installate in
  questo modo. Il controllo lo esegue un'attività pianificata che gira come SYSTEM e che l'agent può solo avviare, su
  una copia che l'agent non può modificare.

### Limiti noti

- Un gateway compromesso può fare sugli host tutto quello che può fare il portale (ad esempio spegnere o eliminare VM,
  o caricare file nelle cartelle consentite). L'agent però non gli offre alcun modo di eseguire comandi arbitrari su un
  host.
- Gli host raggiunti tramite un agent di management dipendono da credenziali di amministratore conservate. Chi possiede
  il database e il key ring del gateway, oppure la memoria dell'agent di management, può ottenerle. Preferisci l'agent
  sull'host.
- I limiti di un gruppo vengono fatti rispettare dal portale. Le modifiche fatte direttamente in Hyper-V non vengono
  impedite.
- Un template contiene tutto quello che c'era sui dischi della VM di origine. Un utente con accesso alla console di una
  VM controlla quel guest come se fosse davanti alla sua tastiera.

L'elenco completo di minacce, contromisure e rischi residui è in [docs/threat-model.md](docs/threat-model.md).
