import { Component } from 'react';
import styles from './Navbar.module.css';
import { TatakiLogo } from '../icons/TatakiLogo';

interface NavbarProps {
  // children: ReactNode;
}

/**
 */
export class Navbar extends Component<NavbarProps> {
  constructor(props: NavbarProps) {
    super(props);
  }

  render() {
    return <header className={styles.navbarHeader}>
      <div className={styles.navbarHeaderLeft}>
        <div className={styles.navbarLogo} aria-hidden>
          <TatakiLogo width={150} height={"auto"}/>
        </div>
        <h2 className={styles.navbarTitle}>Māori Outcomes</h2>
      </div>
      <div className={styles.navbarHeaderRight}>
        {/* TODO: use routing to determine current tab */}
        <button className={`${styles.iconButton} ${styles.active}`} title="Chat">Chat</button>
        <button className={styles.iconButton} title="Share">Share</button>
        <button className={styles.iconButton} title="Info">Info</button>
      </div>
    </header>;
  }
}
