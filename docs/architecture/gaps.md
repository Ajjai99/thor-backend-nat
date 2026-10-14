Backend gaps to raise with the backend team (legacy has these, Thor doesn't):

IdPs can't be enabled or disabled; the only option is deleting them.
  RESOLVED (backend): POST /v1/identity-providers/{name}/enable and /disable (204). New IdPs are created disabled, so after creating one the UI must offer Enable. GET and list responses carry "enabled". IdPs created before this change show as disabled until enabled once.
No role/group mapping (legacy's RBAC section).
SSO users never join the Cognito admins group, so they can't manage IdPs.
No SAML encryption certificate.
  RESOLVED (backend): saml.encryptedResponses and saml.signRequests on create/update, both on by default for new SAML IdPs; leaving them out on update keeps the current setting. GET returns them plus saml.encryptionCertificate (Cognito's per-IdP certificate, same shape as signingCertificates) for the admin to load into their IdP. An IdP that can't encrypt assertions needs encryptedResponses: false.
No test-connection endpoint.
The API doesn't record which SAML source was used, so a file-based setup reopens as Manual.
  RESOLVED (backend): GET returns saml.metadataSource: "metadataUrl", "metadataFile" or "manual" (null for IdPs created before this change).
No vendor label (Entra, Okta, Ping).
  RESOLVED (backend): optional "vendor" on create/update/GET/list, one of entra, okta, ping, google, adfs, other (lower-case). Omitting it on update keeps the current one.
Removing an attribute-mapping row on edit may not delete it in Cognito: the backend passes the mapping straight to Cognito's update call, and I'm not sure whether that call replaces or merges mappings. It's worth one manual test
  RESOLVED (verified on dev through the IdP editor): Cognito replaces the mapping, so a removed row is removed, and an empty mapping clears it. Note Cognito adds "username": "sub" to OIDC IdPs itself. Mapping targets are now limited to email, given_name, family_name, name and username; anything else is a 400 unless the IdP already had that exact mapping.
Every authenticated API call fails in the browser because of CORS: the ANY / and ANY /{proxy+} routes use the custom authorizer, and ANY also catches the browser's OPTIONS preflight, which gets a 401. (Diagnosis confirmed. Correction: those routes came from 7c735938/98d5811d in August; 4e13042 only added the CORS block.)
  RESOLVED (backend): browsers now call the API same-origin at https://<tenant>.<env-domain>/api/... through the frontend CloudFront, and API Gateway has no CORS at all (ADR §4). Frontend: set the API base URL to the relative /api (e.g. /api/v1/identity-providers), and proxy /api to the dev environment from the local dev server; localhost can no longer call api.<env-domain> directly. Connectors keep using api.<env-domain>.
No S3 step exists for SAML metadata: send the file inline as saml.metadataFile in the create/update JSON body. The real limit was the WAF's 8KB body rule, which blocked typical Entra/ADFS metadata.
  RESOLVED (backend): IdP create/update are exempt from the 8KB WAF rule and capped at 256KB by Thor.Api instead.
