import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { FluentProvider, Toaster, webDarkTheme, webLightTheme } from '@fluentui/react-components';
import { AuthProvider } from './auth';
import { App } from './App';
import { TOASTER_ID } from './common';
import './styles.css';

function Root() {
  const query = window.matchMedia('(prefers-color-scheme: dark)');
  const [dark, setDark] = useState(query.matches);
  useEffect(() => {
    const listener = (e: MediaQueryListEvent) => setDark(e.matches);
    query.addEventListener('change', listener);
    return () => query.removeEventListener('change', listener);
  }, [query]);
  useEffect(() => { document.documentElement.dataset.theme = dark ? 'dark' : 'light'; }, [dark]);

  return (
    // No className on the provider: Fluent copies it onto every popup layer it portals to <body>.
    <FluentProvider theme={dark ? webDarkTheme : webLightTheme}>
      <Toaster toasterId={TOASTER_ID} position="top-end" />
      <div className="app-root">
        <BrowserRouter>
          <AuthProvider>
            <App />
          </AuthProvider>
        </BrowserRouter>
      </div>
    </FluentProvider>
  );
}

createRoot(document.getElementById('root')!).render(<StrictMode><Root /></StrictMode>);
