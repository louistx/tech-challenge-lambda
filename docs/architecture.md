# Arquitetura da autenticação

## Escopo

O monólito .NET foi dividido em quatro responsabilidades de repositório, não em quatro serviços de negócio: autenticação Lambda, infraestrutura Kubernetes, infraestrutura do banco gerenciado e aplicação principal. Este repositório implementa somente a primeira responsabilidade e seu API Gateway de autenticação.

A função recebe eventos HTTP API v2 em `TechChallenge.Lambda::TechChallenge.Lambda.Function::FunctionHandler`. `AuthService` coordena CPF, persistência e notificação; `IClientRepository` e `INotifier` permitem trocar o banco/provedor sem alterar o fluxo. Um projeto .NET é suficiente para esse escopo; não foram reproduzidas todas as camadas do monólito.

## Componentes

```mermaid
flowchart LR
    U[Cliente] --> G[API Gateway HTTP API]
    G --> L[Lambda .NET 10]
    L --> R[(PostgreSQL gerenciado - externo)]
    L --> S[Secrets Manager - segredo externo]
    L --> N[SES - identidade externa]
    L --> C[CloudWatch logs e métricas]
    L --> J[JWT RS256]
    J --> K[API em Kubernetes - externa]
    A[GitHub Actions] --> T[Terraform]
    T --> G
    T --> L
    T --> C
```

Terraform define recursos; o diagrama não indica que já estão publicados. Em desenvolvimento, Docker substitui PostgreSQL gerenciado por PostgreSQL local e SES por Mailpit.

## Sequência de autenticação

```mermaid
sequenceDiagram
    actor C as Cliente
    participant G as API Gateway
    participant L as Lambda
    participant B as PostgreSQL
    participant E as SES / Mailpit
    C->>G: POST /auth/login com CPF
    G->>L: Evento HTTP v2 e sourceIp
    L->>B: Limites por IP e CPF (HMAC)
    L->>L: Normaliza e valida dígitos do CPF
    L->>B: Consulta auth_clients
    alt Cliente existente e ativo
        L->>B: Persiste desafio e HMAC do OTP
        L->>E: Código para o e-mail cadastrado
    end
    L-->>C: 202 e challenge_id opaco
    C->>G: POST /auth/verify com id e código
    G->>L: Evento HTTP v2
    L->>B: SELECT FOR UPDATE e validação do desafio
    L->>B: Registra tentativa, consome código e revalida status
    alt Código válido
        L->>L: Assina JWT RS256 com sub interno
        L-->>C: 200 e access_token
    else Expirado, incorreto, bloqueado ou usado
        L-->>C: 401 invalid_challenge
    end
```

OTP: seis dígitos aleatórios, cinco minutos, até cinco tentativas, uso único. HMAC vincula código ao desafio. A transação PostgreSQL confirma tentativas incorretas, evitando que rollback reabra tentativas. O bloqueio de linha impede duas emissões concorrentes para o mesmo desafio.

## Fluxo

```mermaid
flowchart TD
    A[CPF recebido] --> B{CPF válido?}
    B -->|Não| X[400]
    B -->|Sim| C{Cliente existe e está ativo?}
    C -->|Sim| D[Persiste desafio e envia OTP]
    C -->|Não| E[Resposta 202 genérica]
    D --> E
    E --> F[Cliente informa código]
    F --> G{OTP válido e cadastro ativo?}
    G -->|Sim| H[Consome desafio e emite JWT]
    G -->|Não| I[401 genérico]
```

## Infraestrutura e entrega

```mermaid
flowchart LR
    Dev[feature/*] --> PR[Pull Request]
    PR --> CI[Build, formato, testes, auditoria e Terraform]
    CI --> Review[Revisão obrigatória]
    Review --> Merge[Merge develop ou main]
    Merge --> OIDC[GitHub Environment e IAM OIDC]
    OIDC --> Apply[Terraform e versão Lambda]
    Apply --> Smoke[Smoke tests]
    Smoke --> CW[CloudWatch]
```

```mermaid
flowchart TB
    Actions[GitHub Actions] --> Role[IAM OIDC - externo]
    Role --> TF[Terraform]
    TF --> GW[HTTP API e JWT authorizer]
    GW --> Alias[Alias live]
    Alias --> Lambda[Lambda .NET 10 em subnets privadas]
    Lambda --> Secrets[Secrets Manager]
    Lambda --> DB[(PostgreSQL - externo)]
    Lambda --> SES[SES]
    Lambda --> Logs[CloudWatch]
    Event[EventBridge a cada hora] --> Lambda
    GW -. Contrato de integração .-> K8s[Kubernetes - externo]
```

Reserved concurrency padrão 5 e pool Npgsql de até 2 conexões por ambiente de execução. O grupo deve dimensionar conexões e avaliar RDS Proxy. A função não cria VPC, RDS, cluster, DNS nem identidade SES. O agendamento remove desafios expirados e janelas antigas de rate limit.

## Abertura de ordem de serviço — integração externa

```mermaid
sequenceDiagram
    actor C as Cliente
    participant A as Autenticação Lambda
    participant G as Gateway da aplicação externa
    participant K as API .NET em Kubernetes
    participant B as Banco gerenciado
    C->>A: Autenticação CPF + OTP
    A-->>C: JWT
    C->>G: POST ordem de serviço + Bearer JWT + correlation ID
    G->>G: Valida assinatura, iss, aud, exp e scope
    G->>K: Requisição autenticada
    K->>K: Autoriza cliente pelo sub e verifica acesso ao recurso
    K->>B: Persiste ordem vinculada ao cliente
    K-->>C: Ordem criada
```

Este repositório implementa emissão e uma rota de demonstração protegida (`/auth/me`). A abertura de OS, autorização por propriedade, regras de negócio, dashboards de ordens e tracing da aplicação são responsabilidades da API principal.

## Modelo de referência

```mermaid
erDiagram
    AUTH_CLIENTS ||--o{ AUTH_CHALLENGES : identifica
    AUTH_CLIENTS {
        varchar id PK
        varchar cpf UK
        boolean active
        varchar email
        bigint created_at
        bigint updated_at
    }
    AUTH_CHALLENGES {
        varchar id PK
        varchar client_id
        varchar verifier
        bigint expires_at
        integer attempts
        boolean consumed
    }
    AUTH_RATE_LIMITS {
        varchar key PK
        bigint window PK
        integer count
    }
```

`auth_clients` é tabela apenas no ambiente local; em produção, pode ser uma view sobre o cadastro existente. A associação com desafios é lógica, sem FK para uma view de outro repositório. CPF normalizado tem índice único na origem; desafios possuem índice de expiração; `(key, window)` é a chave composta do limitador. Epoch UTC evita ambiguidades de fuso em expiração.

PostgreSQL oferece transações e bloqueio de linha para consumo único do OTP. É a referência de integração, sujeita ao acordo do grupo. O cadastro antigo possui `Documento`/`TipoDocumento` e **não possui status ativo nem datas de cadastro na entidade Cliente inspecionada**: não se deve inventar um status ativo padrão. O contrato deve ser implementado pelo responsável pelo banco/aplicação.
