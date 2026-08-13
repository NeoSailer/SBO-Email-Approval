import json
import ssl
import urllib.request
import urllib.error

url = 'https://TISSIN:50000/b1s/v1/Login'
data = json.dumps({'UserName':'mat01','Password':'1111','CompanyDB':'SBOTSS'}).encode('utf-8')
headers = {'Content-Type':'application/json','Accept':'application/json'}
req = urllib.request.Request(url, data=data, headers=headers, method='POST')
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
try:
    resp = urllib.request.urlopen(req, context=ctx, timeout=15)
    print('status', resp.status)
    print(resp.read().decode('utf-8', errors='replace'))
except urllib.error.HTTPError as e:
    print('HTTPError', e.code, e.reason)
    print(e.read().decode('utf-8', errors='replace'))
except Exception as ex:
    print(type(ex).__name__, ex)
