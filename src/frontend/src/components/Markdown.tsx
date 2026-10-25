import { useMemo, type ReactNode } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import type { Citation } from '../api/types';
import { isSafeHttpUrl } from './safeUrl';

// Minimal mdast shapes for the citation plugin (avoids a direct dependency on unist types).
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  data?: Record<string, unknown>;
}

const SKIP_PARENTS = new Set(['link', 'linkReference', 'inlineCode', 'code']);
const MARKER = /\[(S\d+)\]/g;

function splitText(node: MdNode, known: ReadonlySet<string>): MdNode[] {
  const value = node.value ?? '';
  const out: MdNode[] = [];
  let last = 0;
  for (const m of value.matchAll(MARKER)) {
    const ref = m[1]!;
    if (!known.has(ref)) continue;
    if (m.index > last) out.push({ type: 'text', value: value.slice(last, m.index) });
    out.push({ type: 'citeRef', data: { hName: 'cite-ref' }, children: [{ type: 'text', value: m[0] }] });
    last = m.index + m[0].length;
  }
  if (last === 0) return [node];
  if (last < value.length) out.push({ type: 'text', value: value.slice(last) });
  return out;
}

function transform(node: MdNode, known: ReadonlySet<string>): void {
  if (!node.children || SKIP_PARENTS.has(node.type)) return;
  const next: MdNode[] = [];
  for (const child of node.children) {
    if (child.type === 'text') next.push(...splitText(child, known));
    else {
      transform(child, known);
      next.push(child);
    }
  }
  node.children = next;
}

/** remark plugin: turns "[S1]" into <cite-ref> elements when a citation with that ref exists. */
function remarkCitationRefs(known: ReadonlySet<string>) {
  return () => (tree: MdNode) => transform(tree, known);
}

export function citationAnchorId(idPrefix: string, ref: string): string {
  return `${idPrefix}-${ref}`;
}

interface Props {
  text: string;
  citations?: Citation[];
  /** Unique per message; used for in-page citation anchors. */
  idPrefix: string;
}

/**
 * Assistant content renderer (api-contract section 8): markdown, raw HTML never interpreted (shown as text),
 * images replaced by their alt text, links only for http(s) with target=_blank rel="noopener noreferrer",
 * [Sn] markers linked to the matching citation.
 */
export function Markdown({ text, citations = [], idPrefix }: Props) {
  const byRef = useMemo(() => new Map(citations.map((c) => [c.ref, c])), [citations]);

  const components = useMemo(() => {
    const map = {
      a: ({ href, children }: { href?: string; children?: ReactNode }) =>
        isSafeHttpUrl(href) ? (
          <a href={href} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        ) : (
          <span>{children}</span>
        ),
      img: ({ alt }: { alt?: string }) => (alt ? <span>{alt}</span> : null),
      'cite-ref': ({ children }: { children?: ReactNode }) => {
        const label = typeof children === 'string' ? children : '';
        const ref = label.replace(/^\[|\]$/g, '');
        const citation = byRef.get(ref);
        if (!citation) return <span>{children}</span>;
        const tip = `Source: ${citation.title}`;
        return isSafeHttpUrl(citation.url) ? (
          <a className="cite-ref" href={citation.url} target="_blank" rel="noopener noreferrer" title={tip}>
            {label}
          </a>
        ) : (
          <a className="cite-ref" href={`#${citationAnchorId(idPrefix, ref)}`} title={tip}>
            {label}
          </a>
        );
      },
    };
    return map as unknown as Components;
  }, [byRef, idPrefix]);

  const known = useMemo(() => new Set(byRef.keys()), [byRef]);
  const remarkPlugins = useMemo(() => [remarkCitationRefs(known)], [known]);

  return (
    <div className="markdown">
      <ReactMarkdown
        components={components}
        remarkPlugins={remarkPlugins}
        urlTransform={(url) => (isSafeHttpUrl(url) ? url : '')}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}
