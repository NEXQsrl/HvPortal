import { setNonce } from 'get-nonce';

/**
 * The gateway sends `style-src 'self' 'nonce-…'` and writes the same nonce into <meta property="csp-nonce">.
 * Components that insert a <style> element at runtime (scroll locking in dialogs/menus, Radix viewports) use it.
 * Scripts never use a nonce: script-src stays 'self'.
 */
export const cspNonce: string | undefined = (() => {
  const v = document.querySelector('meta[property="csp-nonce"]')?.getAttribute('content') ?? '';
  return v && v !== '__CSP_NONCE__' ? v : undefined;
})();

if (cspNonce) setNonce(cspNonce);
