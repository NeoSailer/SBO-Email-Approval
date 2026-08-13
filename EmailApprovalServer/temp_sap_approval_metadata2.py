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
opener = urllib.request.build_opener(
    urllib.request.HTTPSHandler(context=ctx),
    urllib.request.HTTPCookieProcessor(cj)
)

login_data = json.dumps({'UserName': username, 'Password': password, 'CompanyDB': company}).encode('utf-8')
login_req = urllib.request.Request(
    f'{service_url}/Login',
    data=login_data,
    headers={'Content-Type': 'application/json', 'Accept': 'application/json'},
    method='POST'
)
with opener.open(login_req, timeout=15) as r:
    print('login status', r.status)

meta_req = urllib.request.Request(f'{service_url}/$metadata', headers={'Accept': 'application/xml'})
with opener.open(meta_req, timeout=60) as r:
    xml = r.read().decode('utf-8', errors='replace')


def print_section(name, text, max_chars=4000):
    idx = text.find(name)
    if idx == -1:
        print(f'NOT FOUND: {name}')
        return
    start = max(0, idx - 200)
    end = text.find('</EntityType>', idx)
    if end == -1:
        end = min(len(text), idx + max_chars)
    else:
        end += len('</EntityType>')
    print(f'--- SECTION {name} ---')
    print(text[start:end])
    print('--- END SECTION ---\n')

print_section('EntityType Name="ApprovalRequest"', xml)
print_section('EntityType Name="ApprovalRequestDecision"', xml)
print_section('EntitySet="ApprovalRequests"', xml)
print_section('EntitySet="ApprovalRequestDecisions"', xml)

# Show surrounding EntityContainer only around approval-related EntitySets
container_start = xml.find('<EntityContainer')
container_end = xml.find('</EntityContainer>', container_start)
if container_start != -1 and container_end != -1:
    container = xml[container_start:container_end+len('</EntityContainer>')]
    for name in ['ApprovalRequests', 'ApprovalRequestDecisions', 'ApprovalStages', 'ApprovalTemplates', 'Users']:
        idx = container.find(name)
        if idx != -1:
            start = max(0, idx - 200)
            end = min(len(container), idx + 200)
            print(f'--- EntityContainer snippet around {name} ---')
            print(container[start:end])
            print('---')
else:
    print('EntityContainer not found')
