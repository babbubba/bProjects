import { RealStack, guardExit } from './support/real-stack/launcher';

// Avvia lo stack reale con modello finto (database isolato, backend, parser reale, modello finto, proxy) e restituisce la funzione di chiusura:
// Playwright la esegue anche dopo prove fallite. Vedi support/real-stack/launcher.ts.
export default async function globalSetup() {
  const stack = await RealStack.start();
  guardExit(stack);
  return async () => {
    await stack.stop();
  };
}
