import os, sys, tempfile, unittest
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import stamp, start_server
from recall import CHUNK, FACTS, ServerDidNotStart, conversation, estimated_tokens, exam, filler, gateway_env, is_right, launch, llama_args, memory_mib, parse_working_set


def replying(answer_for=lambda messages: "ok"):
    """A fake gateway: answers with `answer_for(messages)`, and records what it was sent."""
    calls = []
    def ask(messages):
        calls.append(list(messages))
        return answer_for(messages)
    return ask, calls


class RecallTests(unittest.TestCase):
    def test_there_are_twelve_facts_in_confusable_pairs(self):
        self.assertEqual(len(FACTS), 12)

    def test_right_answer_and_swapped_value(self):
        self.assertTrue(is_right("Invoices are kept for 7 years.", FACTS[1].want))
        self.assertFalse(is_right("Invoices are kept for 2 years.", FACTS[1].want))

    def test_a_prefix_counts_only_as_written_not_as_the_word_it_starts(self):
        orders, customers = FACTS[3].want, FACTS[9].want
        self.assertTrue(is_right("Order ids start with ORD-.", orders))
        self.assertTrue(is_right("The prefix is CUS-", customers))
        self.assertFalse(is_right("Order ids start with a prefix.", orders))  # "Ord" in "Order" is no answer
        self.assertFalse(is_right("Customer ids start with a prefix.", customers))
        self.assertFalse(is_right("They start with ORDER-.", orders))

    def test_gateway_is_pointed_at_the_temporary_server_and_folder(self):
        env = gateway_env("C:/tmp/x", 8099, 5099)
        self.assertEqual(env["Gateway__LlamaUrl"], "http://127.0.0.1:8099")
        self.assertEqual(env["Gateway__Port"], "5099")
        self.assertEqual(env["Gateway__DataDir"], "C:/tmp/x")


class HarnessTests(unittest.TestCase):
    def test_server_runs_the_t480_cpu_settings_on_the_temporary_port(self):
        args = llama_args("l.exe", "m.gguf", 4, 10240)
        self.assertEqual(args[:3], [os.path.abspath("l.exe"), "-m", "m.gguf"])
        joined = " ".join(args)
        for setting in ["-dev none", "-ngl 0", "-t 4", "--poll 0", "-fa off", "-c 10240", "-ctxcp 8", "--cache-ram 1024", "-np 1", "--cache-reuse 256", "--no-webui", "--port 8099"]:
            self.assertIn(setting, joined)

    def test_the_old_cache_flags_can_be_run_for_comparison(self):
        joined = " ".join(llama_args("l.exe", "m.gguf", 4, 16384, cache_ram=2048, ctxcp=32))
        self.assertIn("--cache-ram 2048", joined)
        self.assertIn("-ctxcp 32", joined)

    def test_the_servers_own_output_is_kept_when_asked(self):
        log = os.path.join(tempfile.mkdtemp(), "server.log")
        server = start_server([sys.executable, "-c", "print('created context checkpoint 1 of 8')"], 59998, log)
        server.wait()
        with open(log, encoding="utf-8") as f:
            self.assertIn("created context checkpoint 1 of 8", f.read())

    @unittest.skipUnless(os.name == "nt", "Windows' own memory counters")
    def test_memory_is_read_as_windows_reports_it(self):
        mib = memory_mib(os.getpid())
        self.assertEqual(set(mib), {"working_set", "peak_working_set", "private", "peak_private"})
        self.assertGreater(mib["private"], 0)
        self.assertGreaterEqual(mib["peak_working_set"], mib["working_set"])
        self.assertGreaterEqual(mib["peak_private"], mib["private"])
        self.assertEqual(memory_mib(2 ** 31 - 1), {})  # no such process

    def test_a_run_is_stamped_with_the_code_build_and_model_it_measured(self):
        folder = tempfile.mkdtemp()
        model = os.path.join(folder, "m.gguf")
        with open(model, "wb") as f:
            f.write(b"GGUF" + bytes(3 << 20))
        llama = os.path.join(folder, "llama-server.cmd")
        with open(llama, "w") as f:
            f.write("@echo version: 0.8.0-dev (build 11982, commit 8a199ad31)\n")
        stamped = stamp(llama if os.name == "nt" else "no-such-llama", model)
        self.assertRegex(stamped["git_commit"], r"^[0-9a-f]{40}$")
        self.assertIn(stamped["git_dirty"], (True, False))
        self.assertTrue(stamped["model_fingerprint"].startswith(f"{(3 << 20) + 4}:"))
        if os.name == "nt":
            self.assertEqual(stamped["llama_build"], "11982")

    def test_a_missing_server_or_model_stamps_none_instead_of_failing(self):
        stamped = stamp("no-such-llama-server.exe", "no-such-model.gguf")
        self.assertIsNone(stamped["llama_build"])
        self.assertIsNone(stamped["model_fingerprint"])

    def test_a_relative_server_path_is_made_absolute(self):
        server = llama_args("spike/bin/llama-server.exe", "m.gguf", 4, 16384)[0]
        self.assertTrue(os.path.isabs(server))
        self.assertEqual(server, os.path.abspath(os.path.join("spike", "bin", "llama-server.exe")))

    def test_a_missing_server_program_is_reported_as_not_started(self):
        with self.assertRaises(ServerDidNotStart) as caught:
            launch([os.path.join(HERE, "no-such-llama-server.exe")], 59999)
        self.assertIn("no-such-llama-server.exe", str(caught.exception))

    def test_filler_is_this_repos_code_in_full_chunks_without_generated_files(self):
        chunks = filler()
        self.assertGreaterEqual(len(chunks), len(FACTS))
        self.assertTrue(all(len(c) == CHUNK for c in chunks[:-1]))
        self.assertIn("namespace ", chunks[0])
        self.assertNotIn("<auto-generated>", "".join(chunks))
        self.assertNotIn("\r", chunks[0])  # read the same on a CRLF checkout as anywhere else

    def test_a_conversation_has_one_fact_per_turn_and_no_system_message(self):
        ask, calls = replying()
        history = conversation(ask)
        self.assertEqual([m["role"] for m in history], ["user", "assistant"] * 12)
        self.assertEqual(history[0]["content"], FACTS[0].fact)
        self.assertEqual(len(calls), 12)
        self.assertEqual(calls[1][-1]["content"], FACTS[1].fact)

    def test_a_deep_conversation_puts_a_code_chunk_after_each_fact(self):
        ask, _ = replying()
        history = conversation(ask, ["CHUNK%d" % n for n in range(12)])
        self.assertTrue(history[0]["content"].startswith(FACTS[0].fact + "\n"))
        self.assertIn("```csharp\nCHUNK0\n```", history[0]["content"])
        self.assertIn("CHUNK11", history[22]["content"])

    def test_an_exam_asks_every_question_on_the_same_history(self):
        history = [{"role": "user", "content": FACTS[0].fact}, {"role": "assistant", "content": "ok"}]
        ask, calls = replying(lambda m: "It retries 3 times." if "refund" in m[-1]["content"] else "No idea.")
        result = exam(ask, history)
        self.assertEqual((result["score"], result["of"]), (1, 12))
        self.assertTrue(all(c[:2] == history for c in calls))
        self.assertEqual([c[-1]["content"] for c in calls], [f.question for f in FACTS])
        self.assertTrue(result["answers"][0]["right"])
        self.assertGreaterEqual(result["median_s"], 0)

    def test_a_failed_question_counts_as_wrong_and_the_exam_goes_on(self):
        def ask(messages):
            if "refund" in messages[-1]["content"]:
                raise OSError("connection reset")
            return "ok"
        result = exam(ask, [])
        self.assertEqual(len(result["answers"]), 12)
        self.assertFalse(result["answers"][0]["right"])
        self.assertIn("connection reset", result["answers"][0]["error"])

    def test_tokens_are_estimated_the_way_the_gateway_does(self):
        self.assertEqual(estimated_tokens([{"role": "user", "content": "x" * 8}, {"role": "assistant", "content": "y" * 4}]), 3)

    def test_working_set_is_read_from_tasklist_in_any_locale(self):
        self.assertEqual(parse_working_set('"llama-server.exe","4242","Console","1","9,795,072 K"\r\n'), 9565)
        self.assertEqual(parse_working_set('"llama-server.exe","4242","Console","1","9.795.072 K"'), 9565)
        self.assertEqual(parse_working_set('"llama-server.exe","4242","Console","1","9\xa0795\xa0072 K"'), 9565)
        self.assertIsNone(parse_working_set("INFO: No tasks are running which match the specified criteria."))


if __name__ == "__main__":
    unittest.main()
