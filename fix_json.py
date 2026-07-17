import json

file_path = 'docker/keycloak/analytics-realm-export.json'

with open(file_path, 'r', encoding='utf-8') as f:
    data = json.load(f)

for user in data.get('users', []):
    user['credentials'] = [{
        'type': 'password',
        'value': 'password123',
        'temporary': False
    }]
    user['requiredActions'] = []
    
    # Just to be extremely safe against "Verify Email" or other issues
    user['emailVerified'] = True

with open(file_path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)

print("JSON export fixed successfully.")
