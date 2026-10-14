# Execução e deploy

## Pré-requisitos compartilhados

Antes de implantar, o grupo precisa fornecer: conta AWS e região, duas subnets privadas, SGs, PostgreSQL com o contrato `auth_clients`, tabelas de estado, segredo de autenticação, identidade SES verificada, saída HTTPS, bucket de state criptografado/versionado e roles OIDC. No sandbox SES, os destinatários também precisam estar verificados.

O runtime escolhido é `dotnet10`, compatível com o projeto original. [Suporte oficial AWS a .NET 10](https://aws.amazon.com/about-aws/whats-new/2026/01/aws-lambda-dot-net-10/).

## Branches e configuração GitHub

Repositório remoto informado: https://github.com/louistx/tech-challenge-lambda.
Fluxo de branches: `feature/* → develop → main`, com merge por Pull Request. A implementação está na branch `feature/lambda-auth`.

1. Para um repositório sem branch base, o administrador deve estabelecer uma `main` inicial, sem a implementação, e aplicar proteção antes de integrar o código por PR.
2. Criar `develop` e tornar `main` a branch padrão. Fluxo: `feature/* → develop → main` via PR.
3. Em **Settings → Rules → Rulesets**, incluir `main` e `develop`: exigir PR, uma aprovação, resolução de conversas, status check **validate**, impedir force push e exclusão. Aplicar a administradores e deixar bypass vazio conforme as permissões/plano disponíveis.
4. Em **Settings → Environments**, criar `homolog` (branch `develop`) e `production` (branch `main`). Em produção, exigir revisor e impedir autoaprovação se o plano suportar. O deploy inicia automaticamente após merge e aguarda esse gate quando configurado.
5. Em **Settings → Collaborators**, convidar `soat-architecture` e confirmar aceitação. Convite e aceite não foram executados.

Antes de habilitar deploys, configurar e autorizar os recursos AWS. Em **Settings → Secrets and variables → Actions → Variables**, definir a variável de repositório `AWS_DEPLOY_ENABLED=true` somente após essa preparação. Ausente ou diferente de `true`, o CI executa normalmente e o job de deploy é ignorado.

Variáveis de **cada Environment**:

| Nome | Conteúdo |
|---|---|
| AWS_DEPLOY_ROLE_ARN | Role de deploy restrita àquele ambiente |
| AWS_REGION | Região escolhida |
| TF_STATE_BUCKET | Bucket existente para state |
| TFVARS_JSON | JSON dos valores de `terraform.tfvars.example`, sem segredos |

O workflow seleciona `environment` pela branch. Não inclua `environment` no TFVARS_JSON para evitar sobreposição à seleção. Configuração de exemplo usa placeholders deliberados e não deve ser aplicada sem substituição.

### Confiança OIDC

Criar provider `https://token.actions.githubusercontent.com`, audience `sts.amazonaws.com`, e roles independentes. Política de confiança de produção (substituir ACCOUNT_ID):

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": {"Federated": "arn:aws:iam::ACCOUNT_ID:oidc-provider/token.actions.githubusercontent.com"},
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {"StringEquals": {
      "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
      "token.actions.githubusercontent.com:sub": "repo:louistx/tech-challenge-lambda:environment:production"
    }}
  }]
}
```

Homologação usa `environment:homolog`. As restrições de branch são aplicadas no GitHub Environment; não remover esse controle. A role precisa gerenciar somente recursos `tech-challenge-auth-ENV`, ler metadados da rede e acessar o prefixo do state e lock S3. `iam:PassRole` deve ficar limitado à role da Lambda e `iam:PassedToService=lambda.amazonaws.com`. O responsável AWS deve revisar a política de deploy para os ARNs reais; não conceder AdministratorAccess. A role de execução mínima da função já é definida no Terraform.

## Provisionamento

```bash
dotnet restore --locked-mode
bash scripts/package.sh
cp infra/terraform/terraform.tfvars.example infra/terraform/terraform.tfvars
# Preencher somente identificadores e configurações não secretas.
terraform -chdir=infra/terraform init -backend-config=backend.hcl
terraform -chdir=infra/terraform fmt -check
terraform -chdir=infra/terraform validate
terraform -chdir=infra/terraform plan -out=deployment.plan
# Somente após autorização para criar/alterar recursos pagos:
terraform -chdir=infra/terraform apply deployment.plan
```

Preparar `infra/terraform/backend.hcl` a partir de `backend.hcl.example`, com bucket, região e chave separada por ambiente. Não versionar o arquivo preenchido. O state usa criptografia e lock S3. Chaves privadas/senha/OTP HMAC ficam no Secrets Manager, nunca em tfvars, workflow ou state.

Formato do segredo:

```json
{
  "database_connection": "Host=ENDPOINT_RDS;Database=DB;Username=lambda_auth;Password=SECRET;SSL Mode=VerifyFull;Root Certificate=/var/task/certs/rds-global-bundle.crt",
  "private_key": "PEM_PKCS8_RSA_COM_QUEBRAS_DE_LINHA",
  "otp_secret": "SEGREDO_ALEATORIO_COM_PELO_MENOS_32_CARACTERES"
}
```

O certificado público do RDS foi obtido de `https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem`, SHA-256 `fe45bbebf92ad3e27a583bbb2ddd1553c521ed4d49af5514dc0a40372ea5395c`. Atualizá-lo por PR quando a AWS rotacionar autoridades. Não é chave privada.

Outputs: `api_url`, `issuer`, `audience`, `jwks_url`, `lambda_name`, `lambda_version`, `lambda_alias_arn`, `api_id`, `jwt_authorizer_id`, `lambda_role_arn`. Compartilhar issuer/audience/JWKS com a aplicação e o gateway externos.

O pipeline executa restore com auditoria NuGet, build sem warnings, formatação, testes PostgreSQL, empacotamento e validação Terraform antes do deploy. Usa o mesmo ZIP aprovado pelo job `validate`. A versão publicada é apontada pelo alias `live`. Depois do apply, roda smoke e salva outputs vinculados ao SHA do commit.

## Rollback

Preferir reverter por PR o commit problemático e deixar a pipeline republicar a última versão estável. Em emergência autorizada, apontar o alias `live` para uma versão anterior:

```bash
aws lambda update-alias --function-name NOME_DA_FUNCAO --name live --function-version VERSAO_ANTERIOR
bash scripts/smoke.sh URL_DA_API
```

Em seguida, reconciliar o Terraform por PR para que o próximo apply não restaure a versão defeituosa. Preservar overlap de chaves públicas durante rollback. Segredos externos e alterações de schema não são revertidos por trocar o alias; precisam de plano separado do responsável pelo banco.

## Diagnóstico

| Sintoma | Verificar |
|---|---|
| 503 database_unavailable | SGs, DNS, contrato da view, usuário, TLS e limite de conexões |
| 202 sem código | Mailpit local; no cloud, identidade/sandbox SES, conectividade e NotificationErrors |
| 401 após código correto | Expiração, cinco tentativas, reuso ou cliente desativado |
| JWT recusado na API | issuer exato, audience, kid, scope, relógio e cache JWKS |
| 429 | Aguardar a janela de cinco minutos; não aumentar limites para contornar ataques |
| Erro OIDC | sub do Environment, role ARN, provider, branch permitida e audience |
| Banco local com schema antigo | Aplicar SQL idempotente de estado; não apagar volumes com dados úteis |
| Cache/build no editor | `bin`, `obj`, `.terraform`, `.local`, `TestResults` e `artifacts` são regeneráveis e ignorados |

Deploy AWS, rulesets, Environments e convite ao avaliador dependem da configuração do responsável pelo repositório. Os URLs de nuvem devem ser registrados após a implantação.


## Validação

Validação local em 14/10/2026:

- 76 testes aprovados com PostgreSQL, sem falhas ou testes ignorados.
- Build Release sem warnings; formatação e auditoria NuGet aprovadas.
- Cobertura de linhas: 78,84% (477/605), incluindo adaptadores e host local.
- Fluxo HTTP completo com CPF, OTP, JWT, rota protegida e bloqueio de reutilização aprovado.
- Handler invocado no runtime oficial Lambda .NET 10 pelo emulador local.
- Terraform, OpenAPI, workflow e oito diagramas Mermaid validados localmente.

Os testes incluem concorrência PostgreSQL, SQL injection, tokens adulterados/expirados, claims obrigatórias, rotação e erros sem exposição de dados sensíveis. Serviços AWS e integração com a aplicação principal exigem homologação real.

## Pendências de integração

| Responsável | Entrega necessária |
|---|---|
| Banco | View auth_clients, status real, contato verificado, índices, migrations e conexão TLS |
| Infraestrutura | Rede, roles OIDC, state, segredo, SES, alertas e primeiro deploy autorizado |
| Aplicação/Kubernetes | Validação JWT de clientes, autorização das ordens e correlação |
| GitHub | main/develop protegidas, PR obrigatório, Environments e convite soat-architecture |
| Grupo | Monitoramento global, traces, vídeo e PDF com links reais |

Métricas de ordens de serviço, CPU/memória de Kubernetes e abertura de OS pertencem aos outros repositórios. CloudWatch está preparado neste módulo; a integração global Datadog/New Relic e traces distribuídos permanece a cargo do grupo.

## Demonstração — 3 a 4 minutos

1. Mostrar estrutura .NET e diagrama em `architecture.md`.
2. Rejeitar CPF inválido; iniciar login válido e confirmar código no Mailpit.
3. Demonstrar JWT aceito, token inválido rejeitado e OTP sem possibilidade de reutilização (`bash scripts/e2e.sh`).
4. Mostrar testes, logs JSON, correlation ID e pipeline.
5. Após implantação real, mostrar versão AWS, smoke e monitoramento.

O vídeo completo do grupo deve ter até 15 minutos. O PDF final deve reunir links dos quatro repositórios, vídeo, documentação e confirmação do avaliador.
