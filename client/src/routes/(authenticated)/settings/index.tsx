import { createFileRoute } from '@tanstack/react-router';
import {
  apiKeyQueryOptions,
  useGenerateApiKeyMutation,
  useRevokeApiKeyMutation,
} from '@/queries/apikey.ts';
import { useQuery } from '@tanstack/react-query';
import { useState, useRef } from 'react';
import { KeyIcon } from '@heroicons/react/24/outline';

export const Route = createFileRoute('/(authenticated)/settings/')({
  component: SettingsPage,
  loader: ({ context }) =>
    context.queryClient.ensureQueryData(apiKeyQueryOptions()),
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
    if (!newRawKey) {
      return;
    }
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
    <div className="container">
      <h1 className="text-3xl font-bold mb-6 mt-12">Settings</h1>

      <div className="card bg-base-100 shadow-sm border border-base-300">
        <div className="card-body gap-4">
          <h3 className="card-title text-lg">
            <KeyIcon className="w-5 h-5" /> API Key
          </h3>
          <p className="text-base-content text-sm">
            Use an API key to upload PDFs programmatically without a browser
            session. Pass the key in the{' '}
            <kbd className="kbd kbd-sm">Authorization</kbd> header as{' '}
            <kbd className="kbd kbd-sm">ApiKey &lt;key&gt;</kbd>, or use the{' '}
            <kbd className="kbd kbd-sm">X-Api-Key</kbd> header directly.
          </p>

          {/* Newly generated key — shown once */}
          {newRawKey && (
            <div className="ps-5 py-4 border-l-5 border-primary bg-primary/10 flex flex-col items-start gap-2">
              <p className="font-semibold text-lg">
                Copy your API key now — it won&apos;t be shown again.
              </p>
              <div className="flex w-112 gap-2">
                <input
                  className="input input-bordered input-sm font-mono flex-1 min-w-0 text-base-content"
                  onFocus={(e) => e.currentTarget.select()}
                  readOnly
                  ref={inputRef}
                  value={newRawKey}
                />
              </div>
              <div className="flex gap-2">
                <button
                  className="btn btn-sm btn-primary"
                  onClick={handleCopy}
                  type="button"
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
                <button
                  className="btn btn-sm"
                  onClick={handleDismissKey}
                  type="button"
                >
                  I&apos;ve saved it — dismiss
                </button>
              </div>
            </div>
          )}

          {/* Existing key info */}
          {!newRawKey && apiKeyInfo?.exists && (
            <div className="flex flex-col gap-1 text-sm">
              <span className="text-base-content">
                Active key ending in{' '}
                <kbd className="kbd kbd-sm">…{apiKeyInfo.keyHint}</kbd>
              </span>
              {apiKeyInfo.createdAt && (
                <span className="text-base-content/80">
                  Created{' '}
                  {new Date(apiKeyInfo.createdAt).toLocaleDateString(
                    undefined,
                    {
                      day: 'numeric',
                      month: 'long',
                      year: 'numeric',
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
              onClick={handleGenerate}
              type="button"
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
                onClick={() => revokeDialogRef.current?.showModal()}
                type="button"
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

          {apiKeyInfo?.exists && (
            <p className="text-sm text-base-content/70">
              <a
                className="link link-primary"
                href="/swagger"
                rel="noopener noreferrer"
                target="_blank"
              >
                Open API Documentation (Swagger)
              </a>
            </p>
          )}
        </div>
      </div>

      {/* Revoke confirmation dialog */}
      <dialog className="modal" ref={revokeDialogRef}>
        <div className="modal-box">
          <h3 className="font-bold text-lg">Revoke API Key?</h3>
          <p className="py-4 text-base-content/70">
            Any integrations using this key will stop working immediately. This
            cannot be undone.
          </p>
          <div className="modal-action">
            <button
              className="btn btn-ghost"
              onClick={() => revokeDialogRef.current?.close()}
              type="button"
            >
              Cancel
            </button>
            <button
              className="btn btn-error"
              disabled={revokeMutation.isPending}
              onClick={handleRevoke}
              type="button"
            >
              {revokeMutation.isPending && (
                <span className="loading loading-spinner loading-xs" />
              )}
              Revoke
            </button>
          </div>
        </div>
        <form className="modal-backdrop" method="dialog">
          <button type="submit">close</button>
        </form>
      </dialog>
    </div>
  );
}
