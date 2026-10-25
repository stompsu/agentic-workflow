import type { Citation } from '../api/types';
import { citationAnchorId } from './Markdown';
import { isSafeHttpUrl } from './safeUrl';

/** Sources list. Titles are server text rendered as plain text; links only for http(s) URLs. */
export function Citations({ citations, idPrefix }: { citations: Citation[]; idPrefix: string }) {
  if (citations.length === 0) return null;
  return (
    <section className="citations" aria-label="Sources">
      <h4>Sources</h4>
      <ul>
        {citations.map((c) => (
          <li key={c.ref} id={citationAnchorId(idPrefix, c.ref)}>
            <span className="citation-ref">[{c.ref}]</span>{' '}
            {isSafeHttpUrl(c.url) ? (
              <a href={c.url} target="_blank" rel="noopener noreferrer">
                {c.title}
              </a>
            ) : (
              <span>
                {c.title} <span className="citation-doc">({c.docId})</span>
              </span>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
