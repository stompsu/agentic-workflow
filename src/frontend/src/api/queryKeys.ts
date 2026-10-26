export const queryKeys = {
  workflows: ['workflows'] as const,
  sessions: ['sessions'] as const,
  /** Prefix of every session detail key. */
  sessionAll: ['session'] as const,
  session: (id: string) => ['session', id] as const,
};
