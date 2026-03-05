import { useCallback, useEffect, useState } from 'react';
import { useAuth } from './useAuth';

const apiUrl = import.meta.env.VITE_API_URL || '/api';

export interface AgentMapping {
  value: string;
  label: string;
  agentId: string;
}

export const useAgentMappings = () => {
  const { getAccessToken } = useAuth();
  const [agentMappings, setAgentMappings] = useState<AgentMapping[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<Error | null>(null);

  const getAgentMappingById = useCallback((agentId?: string | null) => {
    if (!agentId) {
      return undefined;
    }

    return agentMappings.find((mapping) => mapping.agentId === agentId);
  }, [agentMappings]);

  const isMaoriOutcomesAgent = useCallback((agentId?: string | null) => {
    const mapping = getAgentMappingById(agentId);

    if (!mapping) {
      return false;
    }

    const value = mapping.value.toLowerCase();
    const label = mapping.label.toLowerCase();

    return value.includes('maori-outcomes') || label.includes('maori outcomes');
  }, [getAgentMappingById]);

  useEffect(() => {
    let isMounted = true;

    const loadMappings = async () => {
      setIsLoading(true);
      setError(null);

      try {
        const token = await getAccessToken();
        const headers: HeadersInit = { 'Content-Type': 'application/json' };

        if (token) {
          headers.Authorization = `Bearer ${token}`;
        }

        const response = await fetch(`${apiUrl}/agents`, { headers });

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }

        const data = await response.json();
        const mappings = Array.isArray(data) ? (data as AgentMapping[]) : [];

        if (isMounted) {
          setAgentMappings(mappings);
        }
      } catch (err) {
        if (isMounted) {
          setAgentMappings([]);
          setError(err instanceof Error ? err : new Error('Failed to load agent mappings'));
        }
      } finally {
        if (isMounted) {
          setIsLoading(false);
        }
      }
    };

    loadMappings();

    return () => {
      isMounted = false;
    };
  }, [getAccessToken]);

  return { agentMappings, isLoading, error, getAgentMappingById, isMaoriOutcomesAgent };
};
