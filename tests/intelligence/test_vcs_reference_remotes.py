"""m-2: pip/npm VCS references normalise to the same remote as the plain URL."""

import pytest

from app.code_intelligence.relationships import _extract_remote

PLAIN = _extract_remote("https://github.com/acme/lib.git")


@pytest.mark.parametrize(
    "declaration",
    [
        "git+https://github.com/acme/lib.git@v1.2.0",
        "git+https://github.com/acme/lib.git@main#egg=lib",
        "git+https://github.com/acme/lib@v2",
        "lib @ git+https://github.com/acme/lib.git@0123abc",
        "https://github.com/acme/lib",
        "git+https://github.com/acme/lib@main#egg=lib",
    ],
)
def test_vcs_reference_forms_match_the_plain_remote(declaration: str) -> None:
    assert PLAIN is not None
    assert _extract_remote(declaration) == PLAIN


def test_a_different_repository_is_not_matched() -> None:
    assert _extract_remote("git+https://github.com/acme/other.git@v1") != PLAIN


def test_a_url_with_a_login_name_is_rejected_not_mangled() -> None:
    # URLs carrying credentials are never matched. The old rule cut this URL at the first "@"
    # (because "@v" appeared) and produced the bogus remote "https://dev".
    assert _extract_remote("git+https://dev@vendor.example/acme/lib.git@v3") is None
