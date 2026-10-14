# Contrato de integração

## Banco de dados

A Lambda consulta uma tabela/view `auth_clients` no `search_path` do usuário de conexão:

| Campo | Tipo retornado | Regra |
|---|---|---|
| id | texto até 36 | Identificador interno estável; UUID deve ser convertido para texto na view |
| cpf | texto de 11 dígitos | Normalizado, único e indexado |
| active | boolean não nulo | Status real e aprovado pelo grupo |
| email | texto | Contato previamente cadastrado e verificado |
| created_at / updated_at | bigint UTC epoch | Modelo local; origem definitiva a alinhar |

Consulta: `SELECT id, active, email FROM auth_clients WHERE cpf = $1`. Nunca há criação automática de cliente durante login. No resgate, consulta `id` e `active` novamente.

No monólito, `Cliente.Documento` e `Cliente.TipoDocumento` identificam CPF/CNPJ/RG. O responsável deve filtrar somente CPF, normalizar o documento e definir status e verificação do e-mail. A Lambda não assume nomes de colunas do monólito nem modifica seus dados.

Aplicar `infra/002-auth-state.sql` uma vez com o usuário de migrations. A função precisa de `SELECT` em `auth_clients` e `SELECT, INSERT, UPDATE, DELETE` em `auth_challenges` e `auth_rate_limits`, além de `USAGE` no schema. Ela não precisa de DDL. Exemplo após criar a role externamente:

```sql
GRANT USAGE ON SCHEMA public TO lambda_auth;
GRANT SELECT ON auth_clients TO lambda_auth;
GRANT SELECT, INSERT, UPDATE, DELETE ON auth_challenges, auth_rate_limits TO lambda_auth;
```

Conectividade: subnets privadas, SG com saída TCP 5432 para o banco e entrada no SG do banco originada no SG da Lambda. Secrets Manager e SES exigem HTTPS por saída controlada ou endpoints compatíveis. Configurar `SSL Mode=VerifyFull;Root Certificate=/var/task/certs/rds-global-bundle.crt` na conexão RDS. O certificado público acompanha o pacote; não utilizar `Trust Server Certificate=true`.

## API

Contrato completo: [OpenAPI](openapi.yaml).

| Método e caminho | Resultado |
|---|---|
| POST `/auth/login` | CPF válido → 202 e challenge_id; não emite JWT |
| POST `/auth/verify` | OTP correto → 200, access_token, token_type, expires_in |
| GET `/auth/me` | JWT válido → sub e scope; rota de demonstração |
| GET `/.well-known/jwks.json` | Chaves RSA públicas |
| GET `/.well-known/openid-configuration` | Metadados para descoberta das chaves pelo gateway |

Erros: `400` entrada inválida, `401` desafio/token inválido, `404` rota inexistente, `413` corpo acima de 4 KiB, `415` Content-Type incompatível, `429` limite, `503` banco/serviço indisponível. Corpo: `{"error":{"code":"invalid_payload"},"correlation_id":"..."}`. Erros gerados pelo próprio API Gateway podem ter seu formato nativo.

O cliente envia `x-correlation-id` de até 64 caracteres alfanuméricos/hífen. Valor ausente/inválido gera novo ID. Encaminhar esse header à API principal. Não usar CPF, e-mail, OTP ou JWT como ID de correlação.

## JWT e aplicação .NET

RS256, RSA de no mínimo 2048 bits, expiração padrão 900 segundos, `kid` no header. Claims: `sub`, `iat`, `nbf`, `exp`, `iss`, `aud`, `jti`, `scope=customer`. CPF/e-mail não são incluídos. `iss` é o output `issuer` do Terraform; `aud` padrão `tech-challenge-api`.

A API principal deve usar um esquema de autenticação de **clientes** separado do login de funcionários já existente no monólito. O token antigo de funcionários usa outra configuração; não basta copiar sua chave simétrica. Exemplo de configuração a aplicar naquele repositório:

```csharp
services.AddAuthentication()
    .AddJwtBearer("Customer", options =>
    {
        options.Authority = configuration["CustomerAuth:Issuer"];
        options.Audience = "tech-challenge-api";
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters.ValidAlgorithms = ["RS256"];
        options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
    });
services.AddAuthorization(options => options.AddPolicy("Customer", policy =>
    policy.AddAuthenticationSchemes("Customer").RequireAuthenticatedUser()
        .RequireClaim("scope", "customer")));
```

Usar a política nas rotas sensíveis e extrair `sub` como identificador do cliente. Verificar que a ordem de serviço pertence ao cliente autenticado; `scope=customer` não concede permissão de administrador. Bloqueio imediato de usuário já autenticado requer consulta de status na aplicação ou mecanismo futuro de revogação. Tokens já emitidos expiram em até 15 minutos.

Caso o grupo use um gateway único, importar/coordenar esses recursos no Terraform antes de migrar a propriedade. Um authorizer é vinculado a seu API ID; outro gateway precisa de seu próprio authorizer usando o mesmo issuer/audience. Não provisionar rotas Kubernetes neste repositório.

## Segredos e rotação

Secrets Manager deve conter **somente** os valores `database_connection`, `private_key` (PEM PKCS#8 RSA) e `otp_secret` (mínimo 32 caracteres aleatórios). O Terraform recebe somente o ARN. A função carrega o segredo no cold start; atualização requer nova versão/reinicialização dos ambientes de execução.

Para rotação, publicar antecipadamente a nova chave **pública** em `PREVIOUS_PUBLIC_JWKS` (objeto `{"keys":[...]}`), aguardar propagação do cache, trocar a chave privada e `JWT_KEY_ID`, mantendo a pública anterior. Esperar pelo menos duas horas de cache do gateway mais a vida útil dos tokens antes de removê-la. Chaves privadas nunca entram em JWKS ou tfvars. A sobreposição de cache segue a [documentação do JWT authorizer AWS](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-jwt-authorizer.html).

## Segurança e observabilidade

Limites compartilhados: 5 logins por CPF/5 minutos e 30 por IP/operação/5 minutos. Identificadores do limitador são HMAC, sem CPF/IP em texto. O API Gateway adiciona throttling global. Janelas fixas permitem rajada na fronteira; avaliar proteção adicional conforme carga real.

Respostas iniciais são iguais para clientes ativos/inativos/inexistentes e falha de envio. Envio é síncrono: diferenças de latência ainda podem permitir inferência; não se afirma resistência completa à enumeração por tempo. Falha SES gera `NotificationErrors`, sem expor cadastro ou código ao solicitante.

Logs JSON incluem request_id, correlation_id, operação permitida, status, erro sanitizado e latência. Métricas EMF: Requests, Failures, AuthSuccess, Latency, DatabaseErrors, NotificationErrors e Timeouts. Terraform cria dashboard e alarmes. `alarm_topic_arn` precisa apontar para um destino existente para entregar notificações.

CloudWatch é o monitoramento implementado neste módulo. Se o grupo escolher Datadog/New Relic para a exigência global, o responsável precisa conectar a integração AWS ao log group/namespace e validar ingestão. **Essa integração externa e traces distribuídos não foram executados**. Métricas de OS, CPU/memória de Kubernetes e tempos por status pertencem aos demais repositórios.
