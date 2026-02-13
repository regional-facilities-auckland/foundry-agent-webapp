import React, { Component } from 'react';
import ReactDOM from 'react-dom';
import styles from './Navbar.module.css';
import { TatakiLogo } from '../icons/TatakiLogo';
import type { AgentMapping } from '../../hooks/useAgentMappings';

interface NavbarProps {
  agentMappings: AgentMapping[];
  onAgentChange?: (areaValue: string, agentId: string) => void;
}

interface NavbarState {
  selectedTitle: string;
  isDropdownOpen: boolean;
  dropdownPosition: { top: number; left: number } | null;
}

/**
 */
export class Navbar extends Component<NavbarProps, NavbarState> {
  private dropdownRef = React.createRef<HTMLDivElement>();
  private buttonRef = React.createRef<HTMLButtonElement>();
  private dropdownMenuRef = React.createRef<HTMLDivElement>();

  constructor(props: NavbarProps) {
    super(props);
    this.state = { 
      selectedTitle: props.agentMappings[0]?.value ?? '', 
      isDropdownOpen: false,
      dropdownPosition: null
    };
  }

  componentDidMount() {
    document.addEventListener('click', this.handleClickOutside);
  }

  componentWillUnmount() {
    document.removeEventListener('click', this.handleClickOutside);
  }

  componentDidUpdate(prevProps: NavbarProps) {
    if (prevProps.agentMappings === this.props.agentMappings) {
      return;
    }

    const firstValue = this.props.agentMappings[0]?.value ?? '';
    const isSelectedValid = this.props.agentMappings.some(
      (mapping) => mapping.value === this.state.selectedTitle
    );

    if (!isSelectedValid && firstValue) {
      this.setState({ selectedTitle: firstValue });
    }
  }

  handleClickOutside = (event: MouseEvent) => {
    const target = event.target as Node;
    if (
      this.dropdownRef.current && 
      !this.dropdownRef.current.contains(target) &&
      this.dropdownMenuRef.current &&
      !this.dropdownMenuRef.current.contains(target)
    ) {
      this.setState({ isDropdownOpen: false, dropdownPosition: null });
    }
  };

  handleSelect = (value: string) => {
    const mapping = this.props.agentMappings.find(m => m.value === value);
    if (mapping && this.props.onAgentChange) {
      this.props.onAgentChange(value, mapping.agentId);
    }
    this.setState({ selectedTitle: value, isDropdownOpen: false, dropdownPosition: null });
  };

  toggleDropdown = () => {
    this.setState((prev): NavbarState => {
      if (!prev.isDropdownOpen && this.buttonRef.current) {
        const rect = this.buttonRef.current.getBoundingClientRect();
        return {
          ...prev,
          isDropdownOpen: true,
          dropdownPosition: {
            top: rect.bottom + 4,
            left: rect.left
          }
        };
      }
      return { 
        ...prev,
        isDropdownOpen: !prev.isDropdownOpen,
        dropdownPosition: null
      };
    });
  };

  renderDropdown() {
    const { selectedTitle, isDropdownOpen, dropdownPosition } = this.state;
    const titleOptions = this.props.agentMappings.map(({ value, label }) => ({ value, label }));
    
    if (!isDropdownOpen || !dropdownPosition) {
      return null;
    }

    const dropdownContent = (
      <div 
        ref={this.dropdownMenuRef}
        className={styles.navbarDropdownMenu}
        style={{
          position: 'fixed',
          top: `${dropdownPosition.top}px`,
          left: `${dropdownPosition.left}px`,
        }}
      >
        {titleOptions.map((opt) => (
          <button
            key={opt.value}
            className={`${styles.navbarDropdownItem} ${selectedTitle === opt.value ? styles.navbarDropdownItemActive : ''}`}
            onClick={() => this.handleSelect(opt.value)}
            role="option"
            aria-selected={selectedTitle === opt.value}
          >
            {opt.label}
          </button>
        ))}
      </div>
    );

    return ReactDOM.createPortal(dropdownContent, document.body);
  }

  render() {
    const { selectedTitle, isDropdownOpen } = this.state;
    const titleOptions = this.props.agentMappings.map(({ value, label }) => ({ value, label }));
    const currentLabel = titleOptions.find((opt) => opt.value === selectedTitle)?.label
      ?? titleOptions[0]?.label
      ?? 'Select agent';

    return (
      <>
        <header className={styles.navbarHeader}>
          <div className={styles.navbarHeaderLeft}>
            <div className={styles.navbarLogo} aria-hidden>
              <TatakiLogo width={150} />
            </div>
            <div className={styles.navbarTitleWrap} ref={this.dropdownRef}>
              <button
                ref={this.buttonRef}
                className={styles.navbarTitleButton}
                onClick={this.toggleDropdown}
                aria-label="Select area"
                aria-expanded={isDropdownOpen}
                aria-haspopup="listbox"
              >
                <span className={styles.navbarTitleText}>{currentLabel}</span>
                <svg
                  className={`${styles.navbarTitleChevron} ${isDropdownOpen ? styles.navbarTitleChevronOpen : ''}`}
                  width="20"
                  height="20"
                  viewBox="0 0 24 24"
                  fill="none"
                  xmlns="http://www.w3.org/2000/svg"
                >
                  <path
                    d="M7 10L12 15L17 10"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            </div>
          </div>
          <div className={styles.navbarHeaderRight}>
            {/* TODO: use routing to determine current tab */}
            <button className={`${styles.iconButton} ${styles.active}`} title="Chat">Chat</button>
            <button className={styles.iconButton} title="Share">Share</button>
            <button className={styles.iconButton} title="Info">Info</button>
          </div>
        </header>
        {this.renderDropdown()}
      </>
    );
  }
}
