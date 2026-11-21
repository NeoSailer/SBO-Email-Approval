# 승인 이력 보존 연동 검증 — 2026-09-19

대상 버전: 1.5.4. 테스트 회사/스키마: `SBOTSS`.
운영 `SBOTSS_NEW` 문서와 설정은 변경하지 않았다. 테스트 프로그램은 서버/알림 작업을 시작하지 않으며 이메일을 보내지 않는다.

## 실제 HANA에서 통과한 항목

- 설정된 HANA 계정으로 테스트 회사 연결.
- `@PVI_APVH`, `@PVI_ARVC` 각각 28개 UDF 확인.
- 초안 322 / WddCode 2의 승인 내역 저장 및 커밋.
- 초안 339 / WddCode 21, 22의 두 승인 요청 모두 저장 및 커밋.
- 승인 요청별 현재 테이블과 이력 테이블의 28개 필드가 일치함을 확인.
- 저장 XML의 승인 단계 수와 WDD1 원본의 모든 필드 값 대조.
- 같은 초안을 다시 저장해도 이력 건수가 증가하지 않음을 확인.
- 존재하지 않는 초안으로 변환 요청 시 보존 단계에서 실패하며 Service Layer 로그인 전에 중단됨을 확인. 이력 건수도 유지됨.

저장한 실제 승인 스냅샷은 보존했다. 테스트에서 승인 상태를 변경하거나 공식문서를 생성하지 않았다.

## 남은 검증

`PVI_Interface` 계정으로 SBOTSS Service Layer 로그인 시 HTTP 401 / SAP -304 (`Fail to NONE-SSO login from SLD.`)가 반환되었다. 따라서 공식문서 변환 API 성공 및 변환 후 승인 이력 유지에 대한 종단간 검증은 아직 미완료다. SBOTSS 로그인 가능한 계정이 준비되면 아래 convert 모드로 검증한다. HANA 검증 성공을 종단간 검증 성공으로 해석하면 안 된다.

## 재실행

검증 프로그램은 지정한 XML에서 연결 정보를 읽되, 프로그램 출력 폴더의 설정에만 회사와 스키마를 `SBOTSS`로 강제한다. 원본 설정 파일은 수정하지 않는다.

```powershell
dotnet run --project EmailApprovalServer.IntegrationTests -- --test-SBOTSS <설정파일경로>
dotnet run --project EmailApprovalServer.IntegrationTests -- --test-SBOTSS <설정파일경로> preserve 339
# 실제 테스트 회사 문서가 생성되는 검증
dotnet run --project EmailApprovalServer.IntegrationTests -- --test-SBOTSS <설정파일경로> convert <승인완료초안키>
```
