# Jazari law book: sources and licences

`law-book.db` is a search index of excerpts from Microsoft's .NET documentation, made by `lawbook/build.py` in
the MyOwnLLM repository. Jazari shows the page each answer drew on, as a `source` row with its learn.microsoft.com address.

| Source | Commit | Folders used |
|---|---|---|
| [dotnet/docs](https://github.com/dotnet/docs) | `40fbd4674d72503f18781f67dec0fa69315bb84a` | `docs/csharp`, `docs/fundamentals`, `docs/core`, `docs/standard`, `docs/architecture`; code from `samples/snippets/csharp`, `samples/snippets/standard`, `samples/snippets/core` |
| [dotnet/AspNetCore.Docs](https://github.com/dotnet/AspNetCore.Docs) | `9dfb3e392d31b33761e7cc5070ea4a7d92082907` | `aspnetcore` |
| [dotnet/EntityFramework.Docs](https://github.com/dotnet/EntityFramework.Docs) | `b0c8daf21d4dd8157e9696e77942278b9722b226` | `entity-framework/core`; code from `samples/core` |

## Licences

- **Text:** © Microsoft Corporation, licensed under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/) (each repository's `LICENSE`).
- **Code samples:** © Microsoft Corporation, licensed under the [MIT License](https://opensource.org/licenses/MIT) (each repository's `LICENSE-CODE`).

## Changes made

The text was changed, not only copied. Pages were cut into pieces of at most 1,000 characters. Only the newest
version's text was kept where a page covers several versions. Included files, images, comments and links were
removed, keeping the link text. Code the pages reference from sample files was copied in, and Visual Basic, F#
and C++ samples were left out. `whats-new`, `what-is-new`, `breaking-changes` and `release-notes` pages were
left out. Text repeated on several pages is kept once, and lists of links such as See also are left out. Each page's title and description are kept to find the page. This is not an official Microsoft product, and Microsoft does not endorse it.
