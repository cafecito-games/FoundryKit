namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth.internal

class_name PkcePairTests
extends RefCounted
uses Test

## RFC 7636 Appendix B: this exact verifier must produce this exact challenge.
const _RFC_VERIFIER: String = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
const _RFC_CHALLENGE: String = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

func test_challenge_matches_the_rfc_7636_vector() -> void:
	Expect.that(PkcePair.challenge_for(_RFC_VERIFIER)).to_equal(_RFC_CHALLENGE)

func test_verifier_is_unreserved_and_within_length() -> void:
	var pair: PkcePair = PkcePair.generate()
	Expect.that(pair.code_verifier.length() >= 43).to_be_true()
	Expect.that(pair.code_verifier.length() <= 128).to_be_true()
	for forbidden: String in ["+", "/", "="]:
		Expect.that(pair.code_verifier.contains(forbidden)).to_be_false()
		Expect.that(pair.code_challenge.contains(forbidden)).to_be_false()

func test_state_is_independent_of_the_verifier() -> void:
	var pair: PkcePair = PkcePair.generate()
	Expect.that(pair.state == pair.code_verifier).to_be_false()
	Expect.that(pair.state.is_empty()).to_be_false()

func test_two_pairs_differ() -> void:
	Expect.that(PkcePair.generate().code_verifier == PkcePair.generate().code_verifier) \
		.to_be_false()
