import { createFileRoute } from '@tanstack/react-router';

export const Route = createFileRoute('/(authenticated)/reports/$fileId/')({
  component: RouteComponent,
});

function RouteComponent() {
  return <div>Hello /(authenticated)/reports/$fileId/!</div>;
}
