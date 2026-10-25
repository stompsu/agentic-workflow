export const queryKeys = {
  workflows: ['workflows'] as const,
  sessions: ['sessions'] as const,
  session: (id: string) => ['session', id] as const,
};
