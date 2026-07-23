import { PublicClientApplication } from '@azure/msal-browser';

let instance: PublicClientApplication | null = null;
let initializedFor = '';

/// Lazily creates (and re-creates only if tenant/client actually change) the MSAL app instance —
/// avoids initializing it before /auth/config tells us whether Entra SSO is even configured.
export async function getMsal(clientId: string, tenantId: string): Promise<PublicClientApplication> {
  const key = `${clientId}:${tenantId}`;
  if (instance && initializedFor === key) return instance;

  instance = new PublicClientApplication({
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId}`,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: 'sessionStorage' },
  });
  await instance.initialize();
  initializedFor = key;
  return instance;
}
