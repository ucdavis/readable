import { createFileRoute } from '@tanstack/react-router';
import {
  apiKeyQueryOptions,
  useGenerateApiKeyMutation,
  useRevokeApiKeyMutation,
} from '@/queries/apikey.ts';
import { useQuery } from '@tanstack/react-query';
import { useState, useRef } from 'react';

export const Route = createFileRoute('/(authenticated)/settings/')({
  loader: ({ context }) =>
    context.queryClient.ensureQueryData(apiKeyQueryOptions()),
  component: SettingsPage,
});

function SettingsPage() {
  const { data: apiKeyInfo, isLoading } = useQuery(apiKeyQueryOptions());
  const generateMutation = useGenerateApiKeyMutation();
  const revokeMutation = useRevokeApiKeyMutation();

  const [newRawKey, setNewRawKey] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const revokeDialogRef = useRef<HTMLDialogElement>(null);

  const handleGenerate = async () => {
    const result = await generateMutation.mutateAsync();
    setNewRawKey(result.rawKey);
    setCopied(false);
  };

  const handleCopy = async () => {
    if (!newRawKey) return;
    await navigator.clipboard.writeText(newRawKey);
    setCopied(true);
  };

  const handleRevoke = async () => {
    revokeDialogRef.current?.close();
    await revokeMutation.mutateAsync();
    setNewRawKey(null);
  };

  const handleDismissKey = () => {
    setNewRawKey(null);
    setCopied(false);
  };

  if (isLoading) {
    return (
      <div className="container py-10 flex justify-center">
        <span className="loading loading-spinner loading-md" />
      </div>
    );
  }

  return (
    <div className="container py-10 max-w-2xl">
      <h2 className="text-2xl font-bold mb-6">Settings</h2>

      <div className="card bg-base-100 shadow-sm border border-base-300">
        <div className="card-body gap-4">
          <h3 className="card-title text-lg">API Key</h3>
          <p className="text-base-content/70 text-sm">
            Use an API key to upload PDFs programmatically without a browser
            session. Pass the key in the{' '}
            <kbd className="kbd kbd-sm">Authorization</kbd> header as{' '}
            <kbd className="kbd kbd-sm">ApiKey &lt;key&gt;</kbd>.
          </p>

          {/* Newly generated key — shown once */}
          {newRawKey && (
            <div className="alert alert-warning flex-col items-start gap-2">
              <p className="font-semibold">
                Copy your API key now — it won&apos;t be shown again.
              </p>
              <div className="flex w-full gap-2">
                <input
                  ref={inputRef}
                  className="input input-bordered input-sm font-mono flex-1 min-w-0 text-black"
                  readOnly
                  value={newRawKey}
                  onFocus={(e) => e.currentTarget.select()}
                />
                <button
                  className="btn btn-sm btn-neutral"
                  type="button"
                  onClick={handleCopy}
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              </div>
              <button
                className="btn btn-sm btn-ghost self-end"
                type="button"
                onClick={handleDismissKey}
              >
                I&apos;ve saved it — dismiss
              </button>
            </div>
          )}

          {/* Existing key info */}
          {!newRawKey && apiKeyInfo?.exists && (
            <div className="flex flex-col gap-1 text-sm">
              <span className="text-base-content/70">
                Active key ending in{' '}
                <kbd className="kbd kbd-sm">…{apiKeyInfo.keyHint}</kbd>
              </span>
              {apiKeyInfo.createdAt && (
                <span className="text-base-content/50">
                  Created{' '}
                  {new Date(apiKeyInfo.createdAt).toLocaleDateString(
                    undefined,
                    {
                      year: 'numeric',
                      month: 'long',
                      day: 'numeric',
                    }
                  )}
                </span>
              )}
            </div>
          )}

          {!newRawKey && !apiKeyInfo?.exists && (
            <p className="text-sm text-base-content/50">No API key yet.</p>
          )}

          {/* Actions */}
          <div className="card-actions justify-start flex-wrap gap-2 pt-2">
            <button
              className="btn btn-primary btn-sm"
              disabled={generateMutation.isPending}
              type="button"
              onClick={handleGenerate}
            >
              {generateMutation.isPending && (
                <span className="loading loading-spinner loading-xs" />
              )}
              {apiKeyInfo?.exists ? 'Regenerate Key' : 'Generate Key'}
            </button>

            {apiKeyInfo?.exists && !newRawKey && (
              <button
                className="btn btn-error btn-outline btn-sm"
                disabled={revokeMutation.isPending}
                type="button"
                onClick={() => revokeDialogRef.current?.showModal()}
              >
                Revoke Key
              </button>
            )}
          </div>

          {generateMutation.isError && (
            <div className="alert alert-error text-sm">
              Failed to generate key. Please try again.
            </div>
          )}
          {revokeMutation.isError && (
            <div className="alert alert-error text-sm">
              Failed to revoke key. Please try again.
            </div>
          )}
        </div>
      </div>

      {/* Revoke confirmation dialog */}
      <dialog ref={revokeDialogRef} className="modal">
        <div className="modal-box">
          <h3 className="font-bold text-lg">Revoke API Key?</h3>
          <p className="py-4 text-base-content/70">
            Any integrations using this key will stop working immediately. This
            cannot be undone.
          </p>
          <div className="modal-action">
            <button
              className="btn btn-ghost"
              type="button"
              onClick={() => revokeDialogRef.current?.close()}
            >
              Cancel
            </button>
            <button
              className="btn btn-error"
              disabled={revokeMutation.isPending}
              type="button"
              onClick={handleRevoke}
            >
              {revokeMutation.isPending && (
                <span className="loading loading-spinner loading-xs" />
              )}
              Revoke
            </button>
          </div>
        </div>
        <form method="dialog" className="modal-backdrop">
          <button type="submit">close</button>
        </form>
      </dialog>
    </div>
  );
}
