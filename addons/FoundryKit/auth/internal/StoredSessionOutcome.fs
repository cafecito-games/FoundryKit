namespace games.cafecito.foundrykit.auth.internal

## How an attempt to read a stored session record ended.
##
## The three cases are kept apart because a caller acts on them differently: a parsed
## record may be restored, while a malformed one and one from an unknown schema are both
## unreadable — and an unreadable record is never going to become readable, so it is
## erased rather than retried. Guessing at either of them would mean restoring tokens this
## build does not fully understand.
##
## Its own file because only one head type may live in a `.fs` file.
enum_name StoredSessionOutcome:
	## The bytes parsed into a record this build understands.
	Parsed(record: StoredSession)
	## The bytes were not a record at all — empty, not JSON, not an object, or missing a
	## field the record cannot be trusted without. The detail names fields, never values.
	Malformed(detail: String)
	## A record, but written under a schema version this build does not know. Reported
	## rather than guessed at: a record written by a future build is not one this build may
	## interpret.
	VersionUnsupported(version: int)
