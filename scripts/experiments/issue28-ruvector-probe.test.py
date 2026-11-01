"""Report gates: absent/failed evidence must never become a passing probe."""
import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location('probe', pathlib.Path(__file__).with_name('issue28-ruvector-probe.py'))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)

class ReportGateTests(unittest.TestCase):
    def test_empty_is_not_evaluated(self):
        self.assertEqual('NOT_EVALUATED', probe.smoke_status({}))
    def test_failed_correctness_blocks_smoke(self):
        self.assertEqual('FAIL', probe.smoke_status({'restart': {'status': 'PASS'}, 'rollback': {'status': 'FAIL'}}))
    def test_not_run_blocks_smoke(self):
        self.assertEqual('FAIL', probe.smoke_status({'rollback': {'status': 'NOT_RUN'}}))
    def test_smoke_is_never_adoption_authority(self):
        self.assertEqual('PASS_SMOKE_ONLY', probe.smoke_status({'rollback': {'status': 'PASS'}}))

if __name__ == '__main__': unittest.main()
