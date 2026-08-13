import json
import ssl
import urllib.request
import urllib.error
import http.cookiejar

service_url = 'https://TISSIN:50000/b1s/v1'
company = 'SBOTSS'
username = 'mat01'
password = '1111'

ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE

cj = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=ctx), urllib.request.HTTPCookieProcessor(cj))

login_data = json.dumps({'UserName': username, 'Password': password, 'CompanyDB': company}).encode('utf-8')
login_req = urllib.request.Request(f'{service_url}/Login', data=login_data, headers={'Content-Type':'application/json', 'Accept':'application/json'}, method='POST')
with opener.open(login_req, timeout=15) as r:
    print('login status', r.status)

meta_req = urllib.request.Request(f'{service_url}/$metadata', headers={'Accept': 'application/xml'})
with opener.open(meta_req, timeout=15) as r:
    xml = r.read().decode('utf-8', errors='replace')

idx = xml.find('ApprovalRequest')
print('ApprovalRequest idx', idx)
if idx != -1:
    start = max(0, idx - 200)
    end = min(len(xml), idx + 2000)
    print(xml[start:end])

print('\n--- FIND ApprovalRequest entity type section ---')
for section in xml.split('</EntityType>'):
    if 'Name="ApprovalRequest"' in section:
        print(section + '</EntityType>')
        break

print('\n--- FIND ApprovalRequestDecision entity type section ---')
for section in xml.split('</EntityType>'):
    if 'Name="ApprovalRequestDecision"' in section:
        print(section + '</EntityType>')
        break

print('\n--- FIND EntityContainer / EntitySet definitions ---')
container_start = xml.find('<EntityContainer')
container_end = xml.find('</EntityContainer>', container_start)
if container_start != -1 and container_end != -1:
    print(xml[container_start:container_end+17])
else:
    print('EntityContainer not found')

print('\n--- TEST API without CompanyDB ---')
for decision_test in ['approve','reject']:
    payload = {'RequestId': '9', 'Decision': decision_test, 'Reason': 'test'}
    data = json.dumps(payload).encode('utf-8')
    req = urllib.request.Request(f'{service_url}/ApprovalRequests', data=data, headers={'Content-Type': 'application/json', 'Accept': 'application/json'}, method='POST')
    print('\nTest payload', payload)
    try:
        with opener.open(req, timeout=15) as r:
            print('status', r.status)
            print(r.read().decode('utf-8', errors='replace'))
    except urllib.error.HTTPError as e:
        print('status', e.code)
        print(e.read().decode('utf-8', errors='replace'))
