package auth

import rego.v1

verified_claims := claims if {
	[_, claims, _] := io.jwt.decode(bearer_token)
	claims.iss == data.auth.issuer
	#claims.exp > time.now_ns() / 1000000000
}

bearer_token := token if {
	parts := split(input.headers.authorization, " ")
	count(parts) == 2
	lower(parts[0]) == "bearer"
	token := parts[1]
}