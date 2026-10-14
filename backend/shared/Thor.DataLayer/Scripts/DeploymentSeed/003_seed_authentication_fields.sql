-- Seeds the fields required by each authentication_type (master.authentication_fields).
-- display_order is the field's position within its type, as shown in the UI.
WITH v(type_name, name, display_name, description, input_type, display_order) AS (VALUES
    ('Local Username and Password', 'Username', 'Username', 'Enter Username', 'string', 1),
    ('Local Username and Password', 'Password', 'Password', 'Enter Password', 'password', 2),
    ('API Key', 'Username', 'Username', 'Enter Username', 'string', 1),
    ('API Key', 'API Key', 'API Key', 'Enter API Key', 'password', 2),
    ('LDAP Authentication', 'Domain', 'Domain FQDN', 'Enter Domain FQDN', 'string', 1),
    ('LDAP Authentication', 'Username', 'Username', 'Enter Username', 'string', 2),
    ('LDAP Authentication', 'Password', 'Password', 'Enter Password', 'password', 3),
    ('Windows Authentication', 'Domain', 'Domain FQDN', 'Enter Domain FQDN', 'string', 1),
    ('Windows Authentication', 'Username', 'Username', 'Enter Username', 'string', 2),
    ('Windows Authentication', 'Password', 'Password', 'Enter Password', 'password', 3),
    ('SSH Key', 'Username', 'Username', 'Enter Username', 'string', 1),
    ('SSH Key', 'Upload Private Key File', 'Upload Private Key File', 'Upload a Private Key File', 'file', 2),
    ('CyberArk', 'Vault Address', 'Vault Address', 'CyberArk Vault Address of the server', 'string', 1),
    ('CyberArk', 'Port', 'Port', 'CyberArk Vault Port of the server', 'number', 2),
    ('CyberArk', 'Upload Certificate', 'Upload Certificate', 'CyberArk Certification Path of the server', 'file', 3),
    ('CyberArk', 'Certificate Password', 'Certificate Password', 'CyberArk Certificate Password', 'password', 4),
    ('OAuth 2.0 Client Credentials', 'Client ID', 'Client ID', 'Enter Client ID', 'string', 1),
    ('OAuth 2.0 Client Credentials', 'Client Secret', 'Client Secret', 'Enter a Client Secret', 'radio,password', 2),
    ('OAuth 2.0 Client Credentials', 'Certificate', 'Certificate', 'Enter a Certificate', 'radio,file', 3),
    ('OAuth 2.0 Client Credentials', 'Certificate Password', 'Certificate Password', 'Enter Certificate Password', 'certificate-password', 4)
),
updated AS (
    UPDATE master.authentication_fields f
    SET display_order = v.display_order
    FROM v
    JOIN master.authentication_types t ON t.name = v.type_name
    WHERE f.type_id = t.id AND f.name = v.name AND f.display_order <> v.display_order
)
INSERT INTO master.authentication_fields (id, type_id, name, display_name, description, input_type, display_order)
SELECT gen_random_uuid(), t.id, v.name, v.display_name, v.description, v.input_type, v.display_order
FROM v
JOIN master.authentication_types t ON t.name = v.type_name
WHERE NOT EXISTS (
    SELECT 1 FROM master.authentication_fields f
    WHERE f.type_id = t.id AND f.name = v.name
);
