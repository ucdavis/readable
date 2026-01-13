import { createRootRouteWithContext, Outlet } from '@tanstack/react-router';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { TanStackRouterDevtools } from '@tanstack/react-router-devtools';
import { RouterContext } from '../main.tsx';
import Footer from '@/components/pdf/Footer.tsx';

const RootLayout = () => (
  <div className="min-h-screen bg-base-200 flex flex-col">
    <main className="flex-1">
      <Outlet />
    </main>

    <Footer />

    <ReactQueryDevtools buttonPosition="top-right" />
    <TanStackRouterDevtools position="bottom-right" />
  </div>
);

export const Route = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
  notFoundComponent: () => <div>404 - Not Found!</div>,
});
