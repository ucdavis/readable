import {
  createRootRouteWithContext,
  Link,
  Outlet,
} from '@tanstack/react-router';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { TanStackRouterDevtools } from '@tanstack/react-router-devtools';
import { RouterContext } from '../main.tsx';
import Footer from '@/components/pdf/Footer.tsx';
import { AnalyticsListener } from '@/shared/analytics/AnalyticsListener.tsx';

const statusBanner = import.meta.env.VITE_STATUS_BANNER;

const RootLayout = () => (
  <div className="min-h-screen bg-base-200 flex flex-col">
    {statusBanner && (
      <div
        className="bg-error text-error-content text-center py-3 px-4 font-semibold text-sm"
        role="status"
      >
        {statusBanner}
      </div>
    )}
    <main className="flex-1">
      <header className="container mb-6">
        <div className="py-2 flex justify-between items-center">
          <Link
            className="flex gap-4 items-center focus:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 rounded-md"
            to="/"
          >
            <img
              alt="Readable logo"
              className="h-10 w-auto"
              src="/readable.svg"
            />
            <div>
              {' '}
              <h1 className="text-xl font-extrabold">Readable</h1>
              <p className="text-sm text-base-content/70">
                PDF Accessibility Conversion Tool
              </p>
            </div>
          </Link>
          <div className="flex gap-4 items-center">
            <Link to="/settings">Settings</Link>
            <Link to="/FAQs">FAQs</Link>
          </div>
        </div>
      </header>
      <AnalyticsListener />
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
