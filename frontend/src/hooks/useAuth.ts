import { useCallback, useMemo } from "react";

/**
 * Authentication hook that fetches tokens from backend.
 * Backend handles all Azure authentication using service principal credentials.
 * 
 * @returns Object with getAccessToken function and authentication status
 */
export const useAuth = () => {
  const getAccessToken = useCallback(async (): Promise<string | null> => {
    try {
      const response = await fetch("/api/auth/token", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
      });

      if (!response.ok) {
        console.error(`Token request failed: ${response.status}`);
        return null;
      }

      const data = await response.json();
      return data.accessToken || null;
    } catch (error) {
      console.error("Token acquisition error:", error);
      return null;
    }
  }, []);

  // Always authenticated in development mode (backend manages credentials)
  const isAuthenticated = useMemo(() => true, []);

  const user = useMemo(
    () => ({
      displayName: "Service Principal",
      name: "Service Principal",
    }),
    []
  );

  return useMemo(
    () => ({
      getAccessToken,
      isAuthenticated,
      user,
    }),
    [getAccessToken, isAuthenticated, user]
  );
};
