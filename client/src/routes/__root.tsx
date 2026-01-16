import {
  createRootRouteWithContext,
  Link,
  Outlet,
} from '@tanstack/react-router';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { TanStackRouterDevtools } from '@tanstack/react-router-devtools';
import { RouterContext } from '../main.tsx';
import Footer from '@/components/pdf/Footer.tsx';

const RootLayout = () => (
  <div className="min-h-screen bg-base-200 flex flex-col">
    <main className="flex-1">
      <header className="container mb-6">
        <div className="py-2 flex justify-between items-center">
          <Link
            className="block focus:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 rounded-md"
            to="/"
          >
            <h1 className="text-xl font-extrabold">Readable</h1>
            <p className="text-sm text-base-content/70">
              PDF Accessibility Conversion Tool
            </p>
          </Link>
          <Link to="/FAQs">FAQs</Link>
        </div>
      </header>
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
