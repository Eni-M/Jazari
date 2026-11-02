import os, sys, tempfile, unittest
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from build import chunks, clean, inline_snippets, keep_latest_version, learn_url, strip_front_matter


class FrontMatterTests(unittest.TestCase):
    def test_front_matter_gives_the_title_and_is_removed(self):
        title, body = strip_front_matter("---\ntitle: Records\nms.date: 1/1/2026\n---\n# Records\nText")
        self.assertEqual(title, "Records")
        self.assertNotIn("ms.date", body)

    def test_without_front_matter_the_first_heading_is_the_title(self):
        title, body = strip_front_matter("# Spans\nText")
        self.assertEqual(title, "Spans")
        self.assertIn("Text", body)

    def test_a_quoted_title_loses_its_quotes(self):
        self.assertEqual(strip_front_matter('---\ntitle: "Records - C#"\n---\nx')[0], "Records - C#")


class VersionTests(unittest.TestCase):
    def test_old_version_blocks_go_and_current_ones_stay(self):
        md = ':::moniker range=">= aspnetcore-10.0"\nnew\n:::moniker-end\n:::moniker range="< aspnetcore-10.0"\nold\n:::moniker-end\nboth'
        out = keep_latest_version(md)
        self.assertIn("new", out); self.assertIn("both", out); self.assertNotIn("old", out)

    def test_a_bounded_range_below_the_latest_goes(self):
        md = ':::moniker range=">= aspnetcore-6.0 < aspnetcore-8.0"\nmiddle\n:::moniker-end\n:::moniker range=">= aspnetcore-8.0"\nlatest\n:::moniker-end'
        out = keep_latest_version(md)
        self.assertIn("latest", out); self.assertNotIn("middle", out)

    def test_an_or_range_holding_the_latest_stays(self):
        md = ':::moniker range="aspnetcore-7.0 || >= aspnetcore-9.0"\nkept\n:::moniker-end\n:::moniker range="aspnetcore-8.0"\ngone\n:::moniker-end'
        out = keep_latest_version(md)
        self.assertIn("kept", out); self.assertNotIn("gone", out)

    def test_moniker_lines_themselves_go(self):
        self.assertNotIn("moniker", keep_latest_version(':::moniker range=">= x-1.0"\na\n:::moniker-end'))


class SnippetTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.root, "snippets"))
        os.makedirs(os.path.join(self.root, "a"))
        self.write("snippets/A.cs", "// <One>\nvar x = 1;\n// </One>\nvar y = 2;")
        self.write("snippets/R.cs", "#region snippet_Two\n    var z = 3;\n#endregion\nvar w = 4;")
        self.write("snippets/L.cs", "l1\nl2\nl3\nl4")
        self.write("a/B.cs", "var b = 5;")
        self.md_path = os.path.join(self.root, "page.md")

    def write(self, rel, text):
        with open(os.path.join(self.root, rel), "w", encoding="utf8") as f:
            f.write(text)

    def test_a_snippet_id_is_inlined_from_its_region(self):
        out = inline_snippets(':::code language="csharp" source="snippets/A.cs" id="One":::', self.md_path, self.root, self.root)
        self.assertIn("```csharp\nvar x = 1;\n```", out); self.assertNotIn("var y", out)

    def test_a_tilde_source_resolves_from_the_docs_root(self):
        out = inline_snippets(':::code language="csharp" source="~/a/B.cs":::', os.path.join(self.root, "a", "deep", "p.md"), self.root, self.root)
        self.assertIn("var b = 5;", out)

    def test_a_missing_snippet_is_dropped_not_fatal(self):
        out = inline_snippets('before\n:::code language="csharp" source="snippets/Gone.cs" id="X":::\nafter', self.md_path, self.root, self.root)
        self.assertIn("before", out); self.assertIn("after", out); self.assertNotIn(":::code", out)

    def test_a_missing_region_is_dropped_not_fatal(self):
        out = inline_snippets(':::code language="csharp" source="snippets/A.cs" id="Nope":::', self.md_path, self.root, self.root)
        self.assertEqual(out.strip(), "")

    def test_old_style_code_with_a_region_name_is_inlined_and_dedented(self):
        out = inline_snippets('[!code-csharp[Main](snippets/R.cs?name=snippet_Two&highlight=1)]', self.md_path, self.root, self.root)
        self.assertIn("```csharp\nvar z = 3;\n```", out); self.assertNotIn("var w", out)

    def test_a_line_range_is_inlined(self):
        out = inline_snippets(':::code language="csharp" source="snippets/L.cs" range="2-3":::', self.md_path, self.root, self.root)
        self.assertIn("l2\nl3", out); self.assertNotIn("l1", out); self.assertNotIn("l4", out)

    def test_an_id_also_names_its_snippet_prefixed_tag(self):
        self.write("snippets/S.cs", "//< SnippetLiterals>\nvar lit = 1;\n// </SnippetLiterals>\nLiterals();")
        out = inline_snippets(':::code language="csharp" source="snippets/S.cs" id="Literals":::', self.md_path, self.root, self.root)
        self.assertIn("var lit = 1;", out); self.assertNotIn("Literals();", out)

    def test_a_path_in_the_wrong_case_still_resolves(self):
        out = inline_snippets(':::code language="csharp" source="Snippets/a.CS" id="One":::', self.md_path, self.root, self.root)
        self.assertIn("var x = 1;", out)

    def test_a_snippet_outside_the_repo_is_not_read(self):
        out = inline_snippets(':::code language="csharp" source="../../../../etc/passwd":::', self.md_path, self.root, self.root)
        self.assertEqual(out.strip(), "")


class CleanTests(unittest.TestCase):
    def test_includes_are_dropped(self):
        out = clean("a\n[!INCLUDE[x](y.md)]\nb [!INCLUDE [z](w.md)] c")
        self.assertNotIn("INCLUDE", out); self.assertNotIn("y.md", out); self.assertIn("a", out); self.assertIn("c", out)

    def test_xrefs_keep_the_type_and_member(self):
        out = clean("Use <xref:Microsoft.EntityFrameworkCore.RelationalQueryableExtensions.FromSql*> or [keyless types](xref:core/modeling/keyless).")
        self.assertIn("RelationalQueryableExtensions.FromSql", out); self.assertNotIn("Microsoft.EntityFrameworkCore", out)
        self.assertIn("keyless types", out); self.assertNotIn("xref", out)

    def test_links_keep_their_text_and_images_go(self):
        out = clean("See [records](records.md). ![diagram](media/a.png)")
        self.assertEqual(out.strip(), "See records.")

    def test_alert_markers_and_comments_go(self):
        out = clean("> [!NOTE]\n> Mind this.\n<!-- hidden -->")
        self.assertNotIn("[!NOTE]", out); self.assertNotIn("hidden", out); self.assertIn("Mind this.", out)

    def test_no_loc_keeps_its_text(self):
        self.assertEqual(clean('a link with text :::no-loc text="GitHub"::: here').strip(), "a link with text GitHub here")

    def test_a_comment_around_code_goes_whole(self):
        out = clean("keep\n<!-- HOLD\n```csharp\nhidden();\n```\n-->\nalso")
        self.assertNotIn("hidden", out); self.assertNotIn("HOLD", out); self.assertIn("also", out)

    def test_link_text_holding_brackets_keeps_its_words(self):
        self.assertEqual(clean("the [`[UnscopedRef]`](../a.md#x) attribute").strip(), "the `[UnscopedRef]` attribute")

    def test_an_xref_with_spaces_is_still_shortened(self):
        self.assertEqual(clean("use <xref: System.Security.Cryptography.Pkcs.SignedCms > now").strip(), "use Pkcs.SignedCms now")

    def test_code_is_left_alone(self):
        self.assertIn("var a = list[0];", clean("```csharp\nvar a = list[0];\n```"))


class ChunkTests(unittest.TestCase):
    def test_chunks_split_at_headings_and_keep_code_fences_whole(self):
        pieces = chunks("Records", "## Value equality\n" + "para\n\n" * 400 + "```csharp\ncode\n```")
        self.assertTrue(all(len(t) <= 1000 for _, t in pieces))
        self.assertTrue(all(t.count("```") % 2 == 0 for _, t in pieces))
        self.assertEqual(pieces[0][0], "Records: Value equality")

    def test_a_long_code_fence_is_cut_and_each_part_closed(self):
        pieces = chunks("T", "```csharp\n" + "var line = 1234567890;\n" * 200 + "```")
        self.assertGreater(len(pieces), 1)
        for _, t in pieces:
            self.assertLessEqual(len(t), 1000)
            self.assertTrue(t.startswith("```csharp\n")); self.assertTrue(t.endswith("```"))

    def test_a_heading_inside_code_is_not_a_heading(self):
        pieces = chunks("T", "## Real\ntext\n```bash\n# comment\n```")
        self.assertEqual([h for h, _ in pieces], ["T: Real"])

    def test_text_before_any_heading_uses_the_page_title(self):
        self.assertEqual(chunks("Records", "Intro text.")[0], ("Records", "Intro text."))

    def test_a_heading_with_nothing_under_it_makes_no_chunk(self):
        self.assertEqual(chunks("T", "# T\n## Empty\n## Full\ntext"), [("T: Full", "text")])


class UrlTests(unittest.TestCase):
    def test_urls_point_at_learn(self):
        self.assertEqual(learn_url("dotnet/docs", "docs/csharp/fundamentals/types/records.md", "value-equality"),
                         "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/records#value-equality")
        self.assertEqual(learn_url("dotnet/AspNetCore.Docs", "aspnetcore/fundamentals/minimal-apis/responses.md", ""),
                         "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses")
        self.assertEqual(learn_url("dotnet/EntityFramework.Docs", "entity-framework/core/querying/sql-queries.md", ""),
                         "https://learn.microsoft.com/ef/core/querying/sql-queries")

    def test_an_index_page_is_its_folder(self):
        self.assertEqual(learn_url("dotnet/EntityFramework.Docs", "entity-framework/core/index.md", ""),
                         "https://learn.microsoft.com/ef/core/")


if __name__ == "__main__":
    unittest.main()
