// Sentinel color KaTeX paints an un-parseable expression with. rehype-katex never throws
// on a bad macro — it renders the raw TeX in this color and carries on — so on its own a
// typo'd/unsupported command would still *ship* (just red). `rehypeKatexStrict` (below)
// scans for this sentinel after KaTeX runs and hard-fails the build, so broken math can
// never reach the site. Kept deliberately lurid + distinctive so a legit \color can't
// collide with it.
export const KATEX_ERROR_COLOR = "#ff00ff";

/**
 * Fail the build if KaTeX could not parse any math on a page. Runs after rehype-katex and
 * walks the produced MathML for the sentinel error color; throws with the offending TeX so
 * the build log points straight at it. This is what turns "renders red" into "won't build".
 * @returns {(tree: any, file: any) => void}
 */
export function rehypeKatexStrict() {
  return (tree, file) => {
    /** @type {string[]} */
    const broken = [];
    /** @param {any} node */
    const walk = (node) => {
      if (node.type === "element" && node.properties?.mathcolor === KATEX_ERROR_COLOR) {
        const text = JSON.stringify(node.children ?? "").match(/"value":"([^"]*)"/);
        broken.push(text ? text[1] : "(unknown expression)");
      }
      for (const child of node.children ?? []) walk(child);
    };
    walk(tree);
    if (broken.length > 0) {
      throw new Error(
        `KaTeX could not parse ${broken.length} math expression(s) in ` +
          `${file.path ?? file.history?.[0] ?? "a content file"}:\n  ` +
          broken.join("\n  ") +
          `\nFix the LaTeX (this compiler is KaTeX — see its supported-functions list).`,
      );
    }
  };
}
