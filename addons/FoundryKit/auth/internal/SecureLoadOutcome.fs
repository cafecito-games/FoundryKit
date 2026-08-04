namespace games.cafecito.foundrykit.auth.internal

## How an attempt to read a stored session from platform secure storage ended.
##
## [code]Absent[/code] is deliberately distinct from [code]Failed[/code]: a first launch
## and a storage error both surface as a failure at the public API, but internally they
## must not be the same value. Collapsing them means a transient storage error looks like
## "no session stored", so the record is silently never retried and never reported.
enum_name SecureLoadOutcome:
	## The store held a record and returned its raw bytes.
	Loaded(bytes: PackedByteArray)
	## The store is reachable and simply holds nothing.
	Absent
	## The read itself failed. [param detail] names the problem, never a token value.
	Failed(detail: String)
