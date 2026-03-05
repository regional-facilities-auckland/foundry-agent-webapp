import { Spinner } from '@fluentui/react-components';
import { ErrorBoundary } from "./components/core/ErrorBoundary";
import { AgentPreview } from "./components/AgentPreview";
import { Navbar } from './components/core/Navbar';
import { useState, useEffect, useCallback, useRef } from "react";
import type { IAgentMetadata } from "./types/chat";
import { useAgentMappings } from "./hooks/useAgentMappings";
import { useAppContext } from './contexts/AppContext';
import "./App.css";

function App() {
  const [agentMetadata, setAgentMetadata] = useState<IAgentMetadata | null>(null);
  const [isLoadingAgent, setIsLoadingAgent] = useState(true);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const { agentMappings, isLoading: isLoadingMappings, isMaoriOutcomesAgent } = useAgentMappings();
  const { dispatch } = useAppContext();
  const hasInitialized = useRef(false);

  // Wrap fetchAgentMetadata in useCallback to make it stable for the effect
  const fetchAgentMetadata = useCallback(async (agentId?: string) => {
    setIsLoadingAgent(true);
    try {
      const apiUrl = import.meta.env.VITE_API_URL || '/api';
      
      // Build URL with optional agentId query parameter
      const url = agentId 
        ? `${apiUrl}/agent?agentId=${encodeURIComponent(agentId)}`
        : `${apiUrl}/agent`;
      
      const response = await fetch(url, {
        headers: {
          'Content-Type': 'application/json'
        }
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const data = await response.json();
      setAgentMetadata(data);
      setSelectedAgentId(agentId || data.id);
      
      // Update document title with agent name
      document.title = data.name ? `${data.name} - Azure AI Agent` : 'Azure AI Agent';
    } catch (error) {
      console.error('Error fetching agent metadata:', error);
      // Fallback data keeps UI functional on error
      const fallbackAgentId = agentId || 'fallback-agent';
      setAgentMetadata({
        id: fallbackAgentId,
        version: '1.0',
        object: 'agent',
        createdAt: Date.now() / 1000,
        name: 'Azure AI Agent',
        description: 'Your intelligent conversational partner powered by Azure AI',
        model: 'gpt-4o-mini',
        metadata: { logo: 'Avatar_Default.svg' }
      });
      setSelectedAgentId(fallbackAgentId);
      document.title = 'Azure AI Agent';
    } finally {
      setIsLoadingAgent(false);
    }
  }, []);

  // Initial load - use default agent (first in mapping or fallback)
  useEffect(() => {
    if (isLoadingMappings || hasInitialized.current) {
      return;
    }

    hasInitialized.current = true;
    const defaultAgentId = agentMappings[0]?.agentId;
    fetchAgentMetadata(defaultAgentId);
  }, [agentMappings, fetchAgentMetadata, isLoadingMappings]);

  // Handle area change from Navbar
  const handleAgentChange = useCallback((areaValue: string, agentId: string) => {
    if (agentId === selectedAgentId) {
      return;
    }

    console.log(`Area changed to: ${areaValue}, loading agent: ${agentId}`);
    dispatch({ type: 'CHAT_CLEAR' });
    setSelectedAgentId(agentId);
    fetchAgentMetadata(agentId);
  }, [dispatch, fetchAgentMetadata, selectedAgentId]);

  return (
    <ErrorBoundary>
      {isLoadingAgent || isLoadingMappings ? (
        <div className="app-container" style={{ 
          display: 'flex', 
          alignItems: 'center', 
          justifyContent: 'center', 
          height: '100vh', 
          flexDirection: 'column', 
          gap: '1rem' 
        }}>
          <Spinner size="large" />
          <p style={{ margin: 0 }}>Loading agent...</p>
        </div>
      ) : agentMetadata ? (
        <div className="app-container">
          <Navbar
            agentMappings={agentMappings}
            selectedAgentId={selectedAgentId}
            onAgentChange={handleAgentChange}
          />

          <div className="main-content">
            <AgentPreview 
              agentId={selectedAgentId || agentMetadata.id}
              agentName={agentMetadata.metadata?.welcomeMessage || agentMetadata.name}
              agentDescription={agentMetadata.metadata?.description || agentMetadata.description || undefined}
              agentLogo={agentMetadata.metadata?.logo}
              starterPrompts={agentMetadata.starterPrompts || undefined}
              showMaoriAgentImage={isMaoriOutcomesAgent(selectedAgentId || agentMetadata.id)}
            />
          </div>
        </div>
      ) : null}
    </ErrorBoundary>
  );
}

export default App;
