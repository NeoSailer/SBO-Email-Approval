import urllib.parse
import http.client

params = urllib.parse.urlencode({
    'requestId': '9',
    'decision': 'approve',
    'approver': '4',
    'sapUser': 'manager',
    'sapPassword': '1111',
    'reason': 'test'
})
conn = http.client.HTTPConnection('127.0.0.1', 5050, timeout=10)
conn.request('POST', '/approval/complete', params, {'Content-Type': 'application/x-www-form-urlencoded'})
res = conn.getresponse()
print('status', res.status)
print('reason', res.reason)
print('headers', res.getheaders())
body = res.read().decode('utf-8', errors='replace')
print('body:', body)
conn.close()
