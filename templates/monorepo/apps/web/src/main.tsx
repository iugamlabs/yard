import React from 'react';
import { createRoot } from 'react-dom/client';
import { Platform } from '@yard/platform';
import '@yard/ui/styles.css';
createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <Platform name="YardApp" base={import.meta.env.BASE_URL} />
  </React.StrictMode>,
);
