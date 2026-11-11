// Update of the portal started from Settings > System (ADR-042, 0.9.4): what the waiting screen knows and how it
// moves from one phase to the next. Pure functions (unit tested); the screen itself is PortalUpdateOverlay.
//
// The gateway that serves this page is the thing being replaced, so nothing here depends on a continuous connection:
// the run is kept in localStorage (it survives the reload after the restart and a new sign-in) and every conclusion
// comes from facts read again from the gateway:
//  - success   = the gateway that answers now reports the requested version (anonymous /api/v1/auth/state);
//  - failure   = the updater wrote a failure after the request (/api/v1/system/updates, needs the session), or the
//                gateway came back, several times in a row, still on another version;
//  - otherwise = still waiting. Never a percentage: the updater reports none.

export type UpdatePhase =
  /** The setup is being downloaded and verified by the gateway (the request is still open). */
  | 'preparing'
  /** Verified and handed to the updater; the gateway still answers. */
  | 'installing'
  /** The gateway does not answer: it is being replaced and restarted. */
  | 'restarting'
  /** The gateway answers with the requested version. */
  | 'success'
  /** The update was not installed; the previous version runs. */
  | 'failed';

export interface UpdateRun {
  target: string;
  from: string;
  /** Browser clock, ms: when the gateway accepted the update (when the request started, while preparing). */
  startedAt: number;
  phase: UpdatePhase;
  /** The gateway stopped answering at least once after the request. */
  sawDown: boolean;
  /** Consecutive answers from a gateway that came back on a version other than the target. */
  backOnOtherVersion: number;
  /** Reason of a failure: the updater's own message, or a code of this module (see FailureCode). */
  message?: string;
  failure?: FailureCode;
  /** The gateway answers but the session is gone: the result page offers to sign in again (no automatic redirect). */
  signedOut?: boolean;
}

export type FailureCode =
  /** The updater reported a failure (message = its text). */
  | 'updater'
  /** The gateway came back on the previous version and the reason could not be read (not signed in). */
  | 'rolled_back';

/** What one poll of the gateway found. */
export interface Observation {
  /** /api/v1/auth/state answered. */
  reachable: boolean;
  /** Version reported by the gateway that answered. */
  version?: string;
  authenticated?: boolean;
  /** install.outcome of /api/v1/system/updates when it could be read (signed in): pending | installed | failed | none. */
  outcome?: string;
  outcomeMessage?: string | null;
}

/** After this the screen says the update is taking longer than expected (it keeps checking). */
export const SLOW_AFTER_MS = 15 * 60 * 1000;
/** A stored run older than this is dropped at load: nobody is waiting for it any more. */
export const FORGET_AFTER_MS = 6 * 60 * 60 * 1000;
/** Answers in a row, on a version other than the target after a restart, before the update is called failed. */
export const ROLLBACK_CONFIRMATIONS = 4;

export const isFinal = (phase: UpdatePhase) => phase === 'success' || phase === 'failed';

const sameVersion = (a: string | undefined, b: string) => !!a && a.split('+')[0] === b.split('+')[0];

/** The run after one more observation. Final phases never change. */
export function advance(run: UpdateRun, obs: Observation): UpdateRun {
  if (isFinal(run.phase) || run.phase === 'preparing') return run;
  if (!obs.reachable) return { ...run, phase: 'restarting', sawDown: true, backOnOtherVersion: 0 };
  const signedOut = obs.authenticated === false;
  if (sameVersion(obs.version, run.target)) return { ...run, phase: 'success', signedOut, message: undefined, failure: undefined };
  if (obs.outcome === 'failed') return { ...run, phase: 'failed', failure: 'updater', message: obs.outcomeMessage ?? undefined, signedOut };
  if (run.sawDown) {
    // Back, but not on the new version: the installer restores the previous binaries when the new ones do not start.
    // Confirmed over several polls, so that a service that is still being replaced is not taken for a failure.
    const count = run.backOnOtherVersion + 1;
    return count >= ROLLBACK_CONFIRMATIONS
      ? { ...run, phase: 'failed', failure: 'rolled_back', backOnOtherVersion: count, signedOut }
      : { ...run, phase: 'installing', backOnOtherVersion: count, signedOut };
  }
  return { ...run, phase: 'installing', signedOut };
}

/** Milliseconds to wait before the next poll: quick at first, slower while the gateway stays down (backoff, capped). */
export function nextDelay(run: UpdateRun, failuresInARow: number): number {
  if (run.phase === 'restarting') return Math.min(10000, Math.round(2000 * 1.5 ** Math.min(failuresInARow, 6)));
  return 3000;
}

export const isSlow = (run: UpdateRun, now: number) => !isFinal(run.phase) && now - run.startedAt > SLOW_AFTER_MS;

const KEY = 'hvp.portalUpdate';

/** The stored run, when there is one worth resuming (null on any unreadable or old value). */
export function loadRun(now: number, storage: Pick<Storage, 'getItem' | 'removeItem'> = window.localStorage): UpdateRun | null {
  try {
    const raw = storage.getItem(KEY);
    if (!raw) return null;
    const r = JSON.parse(raw) as Partial<UpdateRun>;
    const phases: UpdatePhase[] = ['installing', 'restarting', 'success', 'failed'];
    if (typeof r.target !== 'string' || typeof r.from !== 'string' || typeof r.startedAt !== 'number' || !phases.includes(r.phase as UpdatePhase)
      || now - r.startedAt > FORGET_AFTER_MS || now < r.startedAt - 60000) {
      storage.removeItem(KEY);
      return null;
    }
    return {
      target: r.target, from: r.from, startedAt: r.startedAt, phase: r.phase as UpdatePhase, sawDown: r.sawDown === true,
      backOnOtherVersion: typeof r.backOnOtherVersion === 'number' ? r.backOnOtherVersion : 0,
      message: typeof r.message === 'string' ? r.message : undefined, failure: r.failure, signedOut: r.signedOut === true,
    };
  } catch {
    return null;
  }
}

export function saveRun(run: UpdateRun | null, storage: Pick<Storage, 'setItem' | 'removeItem'> = window.localStorage): void {
  try {
    // While preparing nothing was handed to the updater yet: there is nothing to resume after a reload.
    if (!run || run.phase === 'preparing') storage.removeItem(KEY);
    else storage.setItem(KEY, JSON.stringify(run));
  } catch { /* private mode / storage full: the screen still works until the page is reloaded */ }
}
