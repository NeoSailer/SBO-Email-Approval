# EmailApprovalServer 설치 및 운영 안내

## 1. 앱 설명

EmailApprovalServer는 SAP Business One의 결재 대기 문서를 조회하여 승인자에게 이메일을 발송하고, 이메일의 **승인** 또는 **거절** 링크를 통해 SAP 결재를 처리하는 .NET 8 웹 애플리케이션입니다.

주요 기능은 다음과 같습니다.

- SAP HANA에서 결재 대기 문서와 승인자 이메일 조회
- 새로운 결재 요청을 5분 간격으로 확인하여 이메일 발송
- 평일 지정 시간에 미결 건 알림 이메일 재발송
- 이메일에서 승인 또는 거절 화면 제공
- SAP Business One 계정으로 승인 또는 거절 처리
- 구매 오더, 판매 견적, 구매 견적 지원
- 구매 견적의 품목번호는 `DRF1.U_ITEMCODE` 필드 사용
- 처리된 신규 알림은 `ODRF.U_EMAILNOTI`를 `Y`로 갱신
- 상태 확인용 `/health` 엔드포인트 제공

기본 수신 주소와 포트는 `http://0.0.0.0:5050`입니다.

## 2. 설치 전 준비 사항

서버에 다음 구성 요소가 필요합니다.

1. Windows 10/11 또는 Windows Server
2. .NET 8 ASP.NET Core Runtime
3. SAP HANA Client 6.0
4. SAP HANA 및 SAP Service Layer에 연결 가능한 네트워크
5. SMTP 서버에 연결 가능한 네트워크
6. TCP 5050 인바운드 방화벽 규칙
7. 외부에서 접속할 경우 Sophos DNAT 및 방화벽 규칙

설치 여부는 PowerShell에서 확인할 수 있습니다.

```powershell
dotnet --list-runtimes
```

목록에 `Microsoft.AspNetCore.App 8.x`가 있어야 합니다.

## 3. 배포 파일 설치

1. `EmailApprovalServer-20260813.zip`을 대상 서버로 복사합니다.
2. 예를 들어 다음 폴더에 압축을 풉니다.

```text
C:\Apps\EmailApprovalServer
```

3. 기존 버전을 업데이트하는 경우 먼저 작업 스케줄러 작업을 중지합니다.
4. 기존 `Config\appsettings.xml`을 별도 위치에 백업합니다.
5. 새 배포 파일을 복사한 후 운영 설정을 `Config\appsettings.xml`에 반영합니다.

> `Config\appsettings.xml`에는 SAP, HANA 및 SMTP 암호가 포함될 수 있습니다. 일반 사용자에게 파일 읽기 권한을 부여하거나 이메일·메신저로 공유하지 마십시오.

## 4. 설정 파일

설정 파일 위치:

```text
C:\Apps\EmailApprovalServer\Config\appsettings.xml
```

주요 항목은 다음과 같습니다.

### General

```xml
<General>
  <BaseUrl>http://PUBLIC_IP_OR_DNS:5050</BaseUrl>
  <AllowedNetworkPrefix>10.81.0.0/16,192.168.0.0/16</AllowedNetworkPrefix>
  <AllowPrivateNetwork>true</AllowPrivateNetwork>
  <EnableSsl>false</EnableSsl>
</General>
```

- `BaseUrl`: 메일의 승인·거절 링크에 사용되는 접속 주소
- `AllowedNetworkPrefix`: 접속을 허용할 CIDR 목록. 쉼표 또는 세미콜론으로 구분
- 모든 IPv4를 허용하려면 `0.0.0.0/0` 사용
- `AllowPrivateNetwork`: `false`이면 루프백을 제외한 원격 접속을 거부하고, `true`이면 `AllowedNetworkPrefix` 목록으로 원격 접속을 검사
- `EnableSsl`: 현재 앱의 직접 HTTPS 구성용 예약 설정이며, 기본 서비스 주소는 HTTP입니다.

인터넷 전체 허용은 보안 위험이 있으므로 가능하면 승인자의 공인 IP 또는 VPN 대역만 허용하십시오.

### Sap

```xml
<Sap>
  <ServiceLayerUrl>https://SAP_SERVER:50000/b1s/v1</ServiceLayerUrl>
  <CompanyDb>COMPANY_DATABASE</CompanyDb>
  <UserName>SAP_SERVICE_USER</UserName>
  <Password>SECRET</Password>
  <SessionTimeoutSeconds>1800</SessionTimeoutSeconds>
</Sap>
```

### Mail

```xml
<Mail>
  <SmtpHost>smtp.example.com</SmtpHost>
  <SmtpPort>587</SmtpPort>
  <SmtpUser>sender@example.com</SmtpUser>
  <SmtpPassword>SECRET</SmtpPassword>
  <FromAddress>sender@example.com</FromAddress>
  <EnableSsl>true</EnableSsl>
  <TestRecipient></TestRecipient>
  <CcAddress></CcAddress>
</Mail>
```

- `TestRecipient`: 값이 있으면 모든 메일을 실제 승인자 대신 해당 주소로 발송
- 운영 전환 시 `TestRecipient`를 비워야 실제 승인자에게 발송
- `CcAddress`: 여러 주소는 쉼표 또는 세미콜론으로 구분

### Reminder

```xml
<Reminder>
  <Enabled>true</Enabled>
  <RunAtTimes>10:00,15:00</RunAtTimes>
  <TemplateSubject>Pending approval reminder</TemplateSubject>
  <TemplateBody>There are pending approvals that require action.</TemplateBody>
</Reminder>
```

알림은 서버의 로컬 시간을 기준으로 평일에만 실행됩니다. 앱이 해당 시각에 계속 실행 중이어야 합니다.

### Hana

```xml
<Hana>
  <Host>HANA_SERVER</Host>
  <Port>30015</Port>
  <User>HANA_USER</User>
  <Password>SECRET</Password>
  <Schema>COMPANY_SCHEMA</Schema>
</Hana>
```

HANA 사용자는 결재 조회 권한과 `ODRF.U_EMAILNOTI` 갱신 권한이 필요합니다.

## 5. 최초 수동 실행 및 확인

관리자 PowerShell에서 실행합니다.

```powershell
Set-Location C:\Apps\EmailApprovalServer
.\EmailApprovalServer.exe
```

다른 PowerShell 창에서 상태를 확인합니다.

```powershell
Invoke-RestMethod http://127.0.0.1:5050/health
Test-NetConnection 127.0.0.1 -Port 5050
```

정상이면 `/health`에서 `status`가 `ok`로 반환되고 `TcpTestSucceeded`가 `True`로 표시됩니다. 수동 실행은 `Ctrl+C`로 종료합니다.

## 6. Windows 방화벽 설정

관리자 PowerShell에서 TCP 5050을 허용합니다.

```powershell
New-NetFirewallRule `
  -DisplayName "EmailApproval TCP 5050" `
  -Direction Inbound `
  -Protocol TCP `
  -LocalPort 5050 `
  -Action Allow
```

내부 PC에서 서버 사설 IP로 확인합니다.

```powershell
Test-NetConnection SERVER_PRIVATE_IP -Port 5050
```

## 7. Sophos 외부 접속 설정

공인 IP로 접속해야 한다면 Sophos Firewall에서 **Server Access Assistant (DNAT)**를 사용합니다.

1. TCP 5050 서비스 객체를 만듭니다.
2. `규칙 및 정책 → NAT 규칙 → NAT 규칙 추가`로 이동합니다.
3. `Server Access Assistant (DNAT)`를 선택합니다.
4. 내부 서버 IP에 EmailApprovalServer의 사설 IP를 지정합니다.
5. WAN IP에는 공인 IP가 연결된 WAN 인터페이스를 선택합니다.
6. 서비스에 TCP 5050을 선택합니다.
7. 외부 원본을 테스트 시 `Any`로 지정합니다.
8. Inbound DNAT, loopback, reflexive/SNAT 및 WAN→LAN 허용 규칙이 생성되고 활성화됐는지 확인합니다.
9. 연결 확인 후 외부 원본을 승인된 공인 IP 또는 VPN 대역으로 제한합니다.

외부 테스트는 내부 Wi-Fi가 아닌 휴대폰 LTE/5G에서 수행합니다.

```text
http://PUBLIC_IP:5050/health
```

## 8. 작업 스케줄러 등록

앱은 자체적으로 5분마다 신규 결재를 조회하므로 작업 스케줄러에서는 **서버 시작 시 앱을 한 번 실행하고 계속 유지**하도록 구성합니다. 5분마다 새 프로세스를 실행하면 중복 메일이 발생할 수 있습니다.

1. Windows의 **작업 스케줄러**를 엽니다.
2. 오른쪽에서 **작업 만들기**를 선택합니다.
3. 일반 탭을 설정합니다.
   - 이름: `EmailApprovalServer`
   - 사용자 로그온 여부와 관계없이 실행
   - 가장 높은 수준의 권한으로 실행
4. 트리거 탭에서 **시작할 때**를 추가합니다.
5. 동작 탭에서 **프로그램 시작**을 추가합니다.
   - 프로그램/스크립트: `C:\Apps\EmailApprovalServer\EmailApprovalServer.exe`
   - 시작 위치: `C:\Apps\EmailApprovalServer`
6. 조건 탭에서 서버 운영 정책에 맞게 전원 조건을 조정합니다.
7. 설정 탭에서 다음 항목을 권장합니다.
   - 요청 시 작업 실행 허용
   - 작업 실패 시 다시 시작: 1분 간격
   - 다시 시작 시도: 3회 이상
   - 작업이 이미 실행 중이면: **새 인스턴스를 시작하지 않음**
8. 저장 후 작업을 우클릭하여 **실행**합니다.

실행 상태를 확인합니다.

```powershell
Get-Process EmailApprovalServer -ErrorAction SilentlyContinue
Test-NetConnection 127.0.0.1 -Port 5050
```

## 9. 중지, 재시작 및 업데이트

작업 중지:

```powershell
schtasks /End /TN "\EmailApprovalServer"
```

자동 재실행 방지:

```powershell
schtasks /Change /TN "\EmailApprovalServer" /Disable
```

업데이트 절차:

1. 작업을 중지하고 비활성화합니다.
2. 기존 설치 폴더와 `Config\appsettings.xml`을 백업합니다.
3. 새 ZIP의 파일로 교체합니다.
4. 운영용 `Config\appsettings.xml`을 복원하거나 새 항목과 병합합니다.
5. 작업을 활성화하고 실행합니다.
6. `/health`와 테스트 메일을 확인합니다.

```powershell
schtasks /Change /TN "\EmailApprovalServer" /Enable
schtasks /Run /TN "\EmailApprovalServer"
```

## 10. 문제 해결

### 포트가 열리지 않는 경우

```powershell
Get-NetTCPConnection -LocalPort 5050 -State Listen
```

- 결과 없음: 앱이 실행되지 않았거나 시작 중 오류 발생
- `0.0.0.0:5050`: 정상 수신 상태
- 내부 IP 접속은 되지만 공인 IP가 안 됨: Sophos DNAT 또는 loopback 규칙 확인

### 이메일이 발송되지 않는 경우

- SMTP 주소, 포트, 계정 및 SSL 설정 확인
- 서버에서 SMTP 포트 연결 확인
- 승인자의 `OUSR.E_Mail` 값 확인
- `TestRecipient`가 의도대로 설정됐는지 확인

### 결재 데이터가 조회되지 않는 경우

- HANA 호스트와 포트 연결 확인
- HANA 계정의 스키마 조회 권한 확인
- 대상 문서가 승인 대기 상태인지 확인
- 신규 메일은 `ODRF.U_EMAILNOTI`가 `N` 또는 빈 값인 문서만 발송

### 구매 견적 품목번호가 잘못 표시되는 경우

- 구매 견적은 `DRF1.U_ITEMCODE` 값을 사용합니다.
- SAP 화면의 품목번호 UDF에 값이 저장되어 있는지 확인합니다.

## 11. 보안 권장 사항

- 가능하면 5050 포트를 인터넷 전체에 공개하지 말고 VPN 또는 허용 IP 목록을 사용합니다.
- 외부 공개가 필요하면 HTTPS 리버스 프록시와 인증 적용을 권장합니다.
- `Config\appsettings.xml`의 파일 권한을 서비스 실행 계정과 관리자에게만 부여합니다.
- SAP, HANA 및 SMTP 계정은 필요한 최소 권한만 부여합니다.
- 설정 파일과 로그를 외부에 공유할 때 비밀번호와 내부 주소를 제거합니다.
