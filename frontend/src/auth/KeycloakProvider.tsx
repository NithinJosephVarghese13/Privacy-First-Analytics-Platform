import { createContext, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import Keycloak from 'keycloak-js';
import type { KeycloakTokenParsed } from 'keycloak-js';

// Define the shape of our context
interface AuthContextType {
  isAuthenticated: boolean;
  token: string | undefined;
  parsedToken: KeycloakTokenParsed | undefined;
  login: () => void;
  logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

// Define Keycloak configuration based on the Docker setup
const keycloak = new Keycloak({
  url: 'http://localhost:8080',
  realm: 'analytics-platform',
  clientId: 'analytics-web'
});

// A module-level promise cache to ensure keycloak.init() is called exactly once,
// even across React StrictMode double mounts in development.
let initPromise: Promise<boolean> | null = null;

const getInitPromise = (): Promise<boolean> => {
  if (!initPromise) {
    initPromise = keycloak.init({
      onLoad: 'login-required', // Force login on load
      checkLoginIframe: false,
    });
  }
  return initPromise;
};

export const KeycloakProvider = ({ children }: { children: ReactNode }) => {
  const [isInitialized, setIsInitialized] = useState(false);
  const [isAuthenticated, setIsAuthenticated] = useState(false);

  useEffect(() => {
    let active = true;

    getInitPromise()
      .then((authenticated) => {
        if (active) {
          setIsAuthenticated(authenticated);
          setIsInitialized(true);
        }
      })
      .catch((e) => {
        console.error('Keycloak initialization failed', e);
        if (active) {
          setIsInitialized(true);
        }
      });

    keycloak.onTokenExpired = () => {
      keycloak.updateToken(30).catch(() => {
        console.error('Failed to refresh token');
        keycloak.logout();
      });
    };

    return () => {
      active = false;
    };
  }, []);

  if (!isInitialized) {
    return (
      <div className="flex h-screen w-full items-center justify-center bg-zinc-950 text-white">
        <div className="flex flex-col items-center gap-4">
          <div className="h-10 w-10 animate-spin rounded-full border-4 border-emerald-500 border-t-transparent"></div>
          <p className="text-zinc-400 font-medium tracking-wide">Authenticating...</p>
        </div>
      </div>
    );
  }

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated,
        token: keycloak.token,
        parsedToken: keycloak.tokenParsed,
        login: () => keycloak.login(),
        logout: () => keycloak.logout({ redirectUri: window.location.origin }),
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within a KeycloakProvider');
  }
  return context;
};
