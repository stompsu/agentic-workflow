import { useEffect, useRef, type FormEvent, type KeyboardEvent } from 'react';

export const MAX_MESSAGE_CHARS = 4000;

interface Props {
  /** Controlled draft: the parent clears it only once the message was accepted, so a failure never loses the text. */
  value: string;
  onChange: (value: string) => void;
  disabled: boolean;
  /** Why sending is disabled (shown below the box). */
  disabledReason?: string | undefined;
  busy: boolean;
  onSend: (content: string) => void;
  onStop: () => void;
}

export function Composer({ value, onChange, disabled, disabledReason, busy, onSend, onStop }: Props) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const stopRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    // Send turns into Stop while a turn runs: keep keyboard focus on the control that is there, then return to the box.
    if (busy) stopRef.current?.focus();
    else if (!disabled) ref.current?.focus();
  }, [disabled, busy]);

  const trimmed = value.trim();
  const canSend = !disabled && !busy && trimmed.length > 0 && trimmed.length <= MAX_MESSAGE_CHARS;

  function submit(e?: FormEvent) {
    e?.preventDefault();
    if (!canSend) return;
    onSend(trimmed);
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    }
  }

  return (
    <form className="composer" onSubmit={submit}>
      <label htmlFor="message-input" className="sr-only">
        Message
      </label>
      <textarea
        id="message-input"
        ref={ref}
        value={value}
        rows={2}
        maxLength={MAX_MESSAGE_CHARS}
        placeholder="Ask a question (Enter to send, Shift+Enter for a new line)"
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        aria-describedby={disabledReason ? 'composer-hint' : undefined}
      />
      {busy ? (
        <button ref={stopRef} type="button" className="btn btn-secondary" onClick={onStop}>
          Stop
        </button>
      ) : (
        <button type="submit" className="btn" disabled={!canSend}>
          Send
        </button>
      )}
      {disabledReason && (
        <p id="composer-hint" className="hint">
          {disabledReason}
        </p>
      )}
    </form>
  );
}
