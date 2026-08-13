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
print('LOGIN')
with opener.open(login_req, timeout=15) as r:
    print('login status', r.status)
    print(r.read().decode('utf-8', errors='replace'))
print('cookies', [(c.name, c.value) for c in cj])

print('\nMETADATA')
meta_req = urllib.request.Request(f'{service_url}/$metadata', headers={'Accept':'application/xml'})
with opener.open(meta_req, timeout=15) as r:
    xml = r.read().decode('utf-8', errors='replace')
    print(xml[:4000])

print('\nTEST DECISIONS')
for decision_test in ['approve','reject','Approve','Reject','A','R','1','2',1,2,'Y','N', 'Approved', 'Rejected', 'approved', 'rejected']:
    payload = {'RequestId': '9', 'Decision': decision_test, 'Reason': 'test', 'CompanyDB': company}
    data = json.dumps(payload).encode('utf-8')
    req = urllib.request.Request(f'{service_url}/ApprovalRequests', data=data, headers={'Content-Type': 'application/json', 'Accept': 'application/json'}, method='POST')
    print('\n--- testing Decision=', decision_test)
    try:
        with opener.open(req, timeout=15) as r:
            print('status', r.status)
            print(r.read().decode('utf-8', errors='replace'))
    except urllib.error.HTTPError as e:
        print('status', e.code)
        body = e.read().decode('utf-8', errors='replace')
        print(body)
