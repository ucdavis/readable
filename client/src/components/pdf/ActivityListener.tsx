import { useRouter } from '@tanstack/react-router';
import { useEffect } from 'react';

export function AnalyticsListener() {
  const router = useRouter();

  useEffect(() => {
    // Initial page view
    console.log('[Analytics] initial page view:', window.location.pathname);

    window.gtag?.('event', 'page_view', {
      page_path: window.location.pathname,
    });

    // Route change tracking
    return router.subscribe('onResolved', () => {
      const path = window.location.pathname;

      console.log('[Analytics] route resolved:', path);

      window.gtag?.('event', 'page_view', {
        page_path: path,
      });
    });
  }, [router]);

  return null;
}
