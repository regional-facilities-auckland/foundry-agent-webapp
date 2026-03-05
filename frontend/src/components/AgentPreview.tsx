import React, { useState, useMemo, useEffect, useRef } from 'react';
import { ChatInterface } from './ChatInterface';
import { SettingsPanel } from './core/SettingsPanel';
import { useAppState } from '../hooks/useAppState';
import { useAuth } from '../hooks/useAuth';
import { ChatService } from '../services/chatService';
import { useAppContext } from '../contexts/AppContext';
import akiAgentBackground from '../assets/aki-agent-background_cropped.png';
import styles from './AgentPreview.module.css';

interface AgentPreviewProps {
  agentId: string;
  agentName: string;
  agentDescription?: string;
  agentLogo?: string;
  starterPrompts?: string[];
  showMaoriAgentImage?: boolean;
}

export const AgentPreview: React.FC<AgentPreviewProps> = ({
  agentId,
  agentName,
  agentDescription,
  agentLogo,
  starterPrompts,
  showMaoriAgentImage = false
}) => {
  const { chat } = useAppState();
  const { dispatch } = useAppContext();
  const { getAccessToken } = useAuth();
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const previousAgentIdRef = useRef<string | null>(null);
  const resolvedAgentLogo = showMaoriAgentImage ? akiAgentBackground : agentLogo;

  // Create service instances
  const apiUrl = import.meta.env.VITE_API_URL || '/api';
  
  const chatService = useMemo(() => {
    return new ChatService(apiUrl, getAccessToken, dispatch);
  }, [apiUrl, getAccessToken, dispatch]);

  useEffect(() => {
    if (previousAgentIdRef.current && previousAgentIdRef.current !== agentId) {
      chatService.cancelStream();
      chatService.clearChat();
    }

    previousAgentIdRef.current = agentId;
  }, [agentId, chatService]);

  const handleSendMessage = async (text: string, files?: File[]) => {
    await chatService.sendMessage(text, chat.currentConversationId, files, agentId);
  };

  const handleClearError = () => {
    chatService.clearError();
  };

  const handleNewChat = () => {
    chatService.clearChat();
  };

  const handleCancelStream = () => {
    chatService.cancelStream();
  };

  const handleMcpApproval = async (
    approvalRequestId: string,
    approved: boolean,
    previousResponseId: string,
    conversationId: string
  ) => {
    await chatService.sendMcpApproval(approvalRequestId, approved, previousResponseId, conversationId, agentId);
  };

  return (
    <div className={styles.content}>
      <div className={styles.mainContent}>
        <div className={styles.chatPane}>
          <ChatInterface 
            messages={chat.messages}
            status={chat.status}
            error={chat.error}
            streamingMessageId={chat.streamingMessageId}
            onSendMessage={handleSendMessage}
            onClearError={handleClearError}
            onOpenSettings={() => setIsSettingsOpen(true)}
            onNewChat={handleNewChat}
            onCancelStream={handleCancelStream}
            onMcpApproval={handleMcpApproval}
            conversationId={chat.currentConversationId}
            hasMessages={chat.messages.length > 0}
            disabled={false}
            agentName={agentName}
            agentDescription={agentDescription}
            agentLogo={resolvedAgentLogo}
            starterPrompts={starterPrompts}
          />
        </div>

        {showMaoriAgentImage ? (
          <aside className={styles.agentImagePanel} aria-label="Māori outcomes agent image">
            <div className={styles.agentImageContent}>
              <img
                src={akiAgentBackground}
                alt="AKI Māori outcomes agent"
                className={styles.agentImage}
              />
              <p className={styles.agentImageTitle}>Aki, our AI assistant</p>
            </div>
          </aside>
        ) : null}
      </div>
      
      <SettingsPanel
        isOpen={isSettingsOpen}
        onOpenChange={setIsSettingsOpen}
      />
    </div>
  );
};
