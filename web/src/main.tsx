import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { I18nextProvider } from 'react-i18next'
import App from './App'
import { createI18n } from './i18n'
import './styles.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <I18nextProvider i18n={createI18n()}>
      <App />
    </I18nextProvider>
  </StrictMode>,
)
