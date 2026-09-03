package authorization

import rego.v1

default allow := false

allow if {
	claims := data.auth.verified_claims
	input.action in data.authorization.permissions[claims.pid]
}