# Decisões e propostas

Data: 2026-10-14. Decisões locais abaixo descrevem a implementação deste repositório. Acordos envolvendo outros repositórios permanecem propostos ao grupo.

## ADR-001 — C# / .NET 10

**Status:** adotado. **Contexto/problema:** extrair autenticação de um projeto .NET 10 sem mudar de ecossistema. **Alternativas:** .NET 8 ou .NET 10. **Decisão:** .NET 10 em Lambda managed runtime, com um projeto e interfaces nas fronteiras externas. **Justificativa:** continuidade técnica com o monólito. **Benefícios:** ferramentas e linguagem já conhecidas. **Custos/riscos:** cold start e necessidade de validar o pacote no runtime AWS. Não se adotou AOT nesta entrega.

## ADR-002 — JWT RS256

**Status:** adotado no módulo; configuração na API pendente. **Problema:** permitir verificação sem distribuir segredo de assinatura. **Alternativas:** HS256 compartilhado, sessão central, RS256. **Decisão:** RS256, JWKS público, expiração de 15 minutos, sub interno e scope customer. **Benefícios:** validação independente pelo gateway/API. **Custos/riscos:** rotação com overlap e revogação não imediata. O JWT de funcionários do monólito continua sendo um contrato distinto.

## ADR-003 — PostgreSQL e contrato por view

**Status:** referência adotada, modelo definitivo proposto. **Problema:** consultar cadastro real sem acoplar nomes de colunas ao módulo. **Alternativas:** acessar diretamente Cliente, view auth_clients ou API remota. **Decisão:** view/tabela de leitura auth_clients; Npgsql e SQL parametrizado para operações pequenas. **Justificativa:** transações, índice único e SELECT FOR UPDATE atendem consulta e OTP. **Benefícios:** adaptação sem reescrever autenticação. **Custos/riscos:** banco e rede compartilhados; necessidade de status real e e-mail validado. Não há migrations automáticas no cold start.

## ADR-004 — Fronteira da autenticação

**Status:** adotado. **Problema:** separar responsabilidade sem copiar o monólito inteiro. **Alternativas:** continuar autenticação dentro da API ou extrair toda a aplicação. **Decisão:** Lambda somente autentica clientes e publica contrato; OS e autorização de propriedade ficam na API Kubernetes. **Benefícios:** deploy e escala independentes. **Custos/riscos:** integração e versionamento dos contratos entre equipes.

## ADR-005 — GitHub Actions e OIDC

**Status:** workflow preparado, configuração remota pendente. **Problema:** CI/CD por repositório com credenciais temporárias. **Alternativas:** chaves AWS estáticas, deploy manual ou OIDC. **Decisão:** um workflow com validação e deploy condicionado à branch/Environment. **Benefícios:** menos arquivos e nenhum segredo AWS estático. **Custos/riscos:** bootstrap de roles e proteções do GitHub; um workflow por si só não protege branches.

## ADR-006 — Secrets Manager

**Status:** adaptador implementado; segredo externo pendente. **Problema:** proteger chave RSA, segredo HMAC e conexão. **Alternativas:** env com valores, arquivo versionado ou Secrets Manager. **Decisão:** ARN no ambiente e leitura no cold start. **Benefícios:** privilégio mínimo e segredos fora do state. **Custos/riscos:** cache até reinicialização; rotação requer coordenar nova versão.

## ADR-007 — OTP adicional ao CPF

**Status:** adotado para segurança. **Problema:** conhecer um CPF válido não comprova identidade. **Alternativas:** CPF isolado, senha ou código no contato cadastrado. **Decisão:** OTP antes do JWT; Mailpit local e SES em nuvem. **Benefícios:** comprovação adicional, expiração, uso único e limite de tentativas. **Custos/riscos:** dependência de entrega, contato previamente verificado e inferência residual por latência. OTP é um passo adicional ao requisito mínimo acadêmico.

## ADR-008 — HTTP síncrono

**Status:** adotado no módulo. **Problema:** autenticação precisa responder ao solicitante. **Alternativas:** fila assíncrona ou HTTP. **Decisão:** HTTP API v2 e JSON; envio de código via adaptador síncrono com deadline. **Benefícios:** contrato direto e demonstração simples. **Custos/riscos:** entrega influencia latência; escala futura pode exigir outbox/fila. A integração com OS continua HTTP no repositório principal.

## RFC-001 — Autenticação serverless

**Status:** proposta de integração. **Contexto/objetivo:** centralizar autenticação de clientes. **Requisitos/proposta:** CPF válido, cadastro ativo, OTP, RS256 e consumidor autenticado. **Alternativas:** Cognito ou autenticação na aplicação. **Impactos/riscos:** novo fluxo de login e e-mails verificados. **Plano:** validar localmente, configurar SES/segredo, homologar gateway e API. **Aceite:** login ponta a ponta e rejeição de tokens inválidos na aplicação. **Dependências/perguntas:** quem verifica o contato e qual o frontend/origin definitivo?

## RFC-002 — Banco gerenciado

**Status:** proposta ao responsável pelo banco. **Objetivo:** disponibilizar auth_clients e tabelas de estado. **Requisitos/proposta:** consulta indexada por CPF, active confiável, usuário mínimo e TLS VerifyFull. **Alternativas:** API de cadastro ou acesso direto às tabelas atuais. **Impactos/riscos:** modelo antigo não tem status/datas; conexões Lambda podem esgotar capacidade. **Plano:** aprovar campos, criar view/índices/roles, aplicar SQL de estado e testar concorrência. **Aceite:** lookup e OTP atômico no gerenciado. **Dependências/perguntas:** significado de ativo, schema e necessidade de RDS Proxy.

## RFC-003 — Gateway e Kubernetes

**Status:** proposta à aplicação/infra. **Objetivo:** proteger rotas de clientes usando o novo issuer. **Requisitos/proposta:** JWT authorizer no gateway e esquema Customer na API .NET. **Alternativas:** gateway único ou gateways separados. **Impactos/riscos:** conflito de ownership Terraform e mistura com tokens de funcionário. **Plano:** escolher gateway, configurar descoberta JWKS, adicionar políticas e autorizar propriedade da OS. **Aceite:** 401 sem JWT/expirado e bloqueio de acesso à OS de outro cliente. **Dependências/perguntas:** rotas públicas, DNS e topologia do gateway principal.

## RFC-004 — Ambientes e CI/CD

**Status:** proposta de governança. **Objetivo:** deploy reproduzível em homologação/produção. **Requisitos/proposta:** PR obrigatório, checks, Environments, state separado e OIDC. **Alternativas:** GitLab CI ou deploy manual. **Impactos/riscos:** permissões e restrições do plano GitHub. **Plano:** bootstrap remoto, rulesets, roles e primeira execução em homologação. **Aceite:** evidência de pipeline real, smoke e rollback. **Dependências/perguntas:** aprovadores e conta/região.

## RFC-005 — Observabilidade

**Status:** integração global pendente. **Objetivo:** correlacionar autenticação e consumo da API. **Requisitos/proposta:** logs JSON, correlation ID, métricas EMF/CloudWatch; conectar a ferramenta escolhida pelo grupo. **Alternativas:** integração AWS Datadog, New Relic ou stack OpenTelemetry. **Impactos/riscos:** custo, acesso aos logs e exposição de dados pessoais. **Plano:** selecionar ferramenta, provisionar integração externa, ingerir namespace e demonstrar falhas/latência. **Aceite:** dashboard global e correlação verificável entre serviços. **Dependências/perguntas:** ferramenta escolhida, retenção, destinos de alertas e responsabilidade pelos traces. Métricas de OS e Kubernetes são externas à Lambda.
