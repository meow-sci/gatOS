// @ts-check
import { defineConfig } from "astro/config";
import starlight from "@astrojs/starlight";
import remarkMath from "remark-math";
import rehypeKatex from "rehype-katex";
import { KATEX_ERROR_COLOR, rehypeKatexStrict } from "./rehype-katex-strict.mjs";
import { unified } from "@astrojs/markdown-remark";

// https://astro.build/config
export default defineConfig({
  // Published on GitHub Pages under the meow-sci org's custom domain, served at
  // https://meow.science.fail/gatOS/ (sibling project flexo lives at /flexo/).
  // `site` + `base` make Starlight emit correct absolute/prefixed URLs; the base
  // must match the repo name exactly (case-sensitive path segment).
  site: "https://meow.science.fail",
  base: "/gatOS/",
  integrations: [
    starlight({
      editLink: {
        baseUrl: "https://github.com/meow-sci/gatOS/edit/main/",
      },
      title: "gatOS",
      customCss: ["./src/styles/custom.css"],
      social: [{ icon: "github", label: "GitHub", href: "https://github.com/meow-sci/gatOS" }],
      sidebar: [
        {
          label: "Guides",
          items: [{ autogenerate: { directory: "guides" } }],
        },
        {
          label: "Reference",
          items: [{ autogenerate: { directory: "reference" } }],
        },
      ],
    }),
  ],
  // this will setup Markdown/MDX LaTeX -> MathML at build time
  markdown: {
    processor: unified({
      remarkPlugins: [remarkMath],
      rehypePlugins: [
        [rehypeKatex, { output: "mathml", errorColor: KATEX_ERROR_COLOR }],
        rehypeKatexStrict,
      ],
    }),
  },
});
