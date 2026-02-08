import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
import { AppProvider } from './contexts/AppContext';
import { ThemeProvider } from './components/ThemeProvider';

const rootElement = document.getElementById("root");

if (!rootElement) {
  console.error('Failed to find the root element');
} else {
  ReactDOM.createRoot(rootElement).render(
    <React.StrictMode>
      <AppProvider>
        <ThemeProvider>
          <App />
        </ThemeProvider>
      </AppProvider>
    </React.StrictMode>
  );
}
