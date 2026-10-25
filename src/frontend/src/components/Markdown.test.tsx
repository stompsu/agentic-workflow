import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Markdown } from './Markdown';
import { Citations } from './Citations';
import { AgentBadge, UserBubble } from './Messages';
import type { Citation } from '../api/types';

const cit = (over: Partial<Citation> = {}): Citation => ({ ref: 'S1', title: 'Claims handling times', docId: 'd-1', url: 'https://kb.example.com/c', ...over });

describe('Markdown rendering safety (api-contract section 8)', () => {
  it('renders markdown formatting', () => {
    const { container } = render(<Markdown idPrefix="t" text={'**bold** and `code`\n\n- a\n- b'} />);
    expect(container.querySelector('strong')?.textContent).toBe('bold');
    expect(container.querySelector('code')?.textContent).toBe('code');
    expect(container.querySelectorAll('li')).toHaveLength(2);
  });

  it('never renders images (alt text only)', () => {
    const { container } = render(<Markdown idPrefix="t" text={'![diagram alt](https://evil.example/x.png) and ![](https://evil.example/y.png)'} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.textContent).toContain('diagram alt');
    expect(container.innerHTML).not.toContain('evil.example');
  });

  it('strips javascript: and other non-http(s) links to plain text', () => {
    const { container } = render(
      <Markdown idPrefix="t" text={'[click](javascript:alert(1)) [mail](mailto:a@b.c) [data](data:text/html,x) [rel](/relative) [ok](https://ok.example/p)'} />,
    );
    const anchors = [...container.querySelectorAll('a')];
    expect(anchors).toHaveLength(1);
    expect(anchors[0]?.getAttribute('href')).toBe('https://ok.example/p');
    expect(container.innerHTML).not.toContain('javascript:');
    expect(container.textContent).toContain('click');
  });

  it('opens http(s) links in a new tab with noopener noreferrer', () => {
    render(<Markdown idPrefix="t" text="[a](http://a.example/) [b](https://b.example/)" />);
    for (const a of screen.getAllByRole('link')) {
      expect(a).toHaveAttribute('target', '_blank');
      expect(a).toHaveAttribute('rel', 'noopener noreferrer');
    }
  });

  it('escapes raw HTML (no elements are created)', () => {
    const { container } = render(
      <Markdown idPrefix="t" text={'<script>window.__x=1</script><b onclick="x()">bold</b> <iframe src="https://evil.example"></iframe>'} />,
    );
    expect(container.querySelector('script, iframe, b')).toBeNull();
    expect(container.querySelector('[onclick]')).toBeNull();
    expect((window as unknown as { __x?: number }).__x).toBeUndefined();
  });

  it('links [Sn] markers to the citation url, or to the in-page entry when there is no url', () => {
    const { container } = render(
      <Markdown idPrefix="m1" text="See [S1] and [S2] and [S9]." citations={[cit(), cit({ ref: 'S2', url: null, title: 'No link' })]} />,
    );
    const links = [...container.querySelectorAll('a.cite-ref')];
    expect(links).toHaveLength(2);
    expect(links[0]).toHaveAttribute('href', 'https://kb.example.com/c');
    expect(links[0]).toHaveAttribute('target', '_blank');
    expect(links[1]).toHaveAttribute('href', '#m1-S2');
    expect(container.textContent).toContain('[S9]'); // unknown marker stays plain text
  });

  it('does not turn a citation url with an unsafe scheme into a link', () => {
    const { container } = render(<Citations idPrefix="m" citations={[cit({ url: 'javascript:alert(1)' })]} />);
    expect(container.querySelector('a')).toBeNull();
    expect(container.textContent).toContain('Claims handling times');
  });
});

describe('plain-text fields', () => {
  it('renders citation titles as plain text (no HTML)', () => {
    const { container } = render(<Citations idPrefix="m" citations={[cit({ title: '<img src=x onerror=alert(1)> **b**', url: null })]} />);
    expect(container.querySelector('img, b, strong')).toBeNull();
    expect(container.textContent).toContain('<img src=x onerror=alert(1)> **b**');
  });

  it('renders agent names and user messages as plain text', () => {
    const { container } = render(
      <ul>
        <AgentBadge name="<i>Agent</i>" />
        <UserBubble content={'**not bold** <script>x</script> [l](https://a.example)'} />
      </ul>,
    );
    expect(container.querySelector('i, strong, script, a')).toBeNull();
    expect(container.textContent).toContain('<i>Agent</i>');
    expect(container.textContent).toContain('**not bold** <script>x</script> [l](https://a.example)');
  });
});
