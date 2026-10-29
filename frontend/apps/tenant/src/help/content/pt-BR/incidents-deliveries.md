# Consulte incidentes e entregas

## Pré-requisitos

Usuários do tenant podem consultar incidentes e entregas. É necessário acesso **Gerenciar** para tentar novamente uma entrega esgotada. O processamento exige serviço do tenant com SQL, worker da fila, destino acessível e provedor funcional para cabeçalhos autenticados.

## Permissão

A página Alertas separa incidentes e tentativas de entrega. Usuários com acesso de Leitura podem consultar registros e códigos seguros. Somente usuários com acesso Gerenciar podem alterar regras ou destinos e solicitar nova tentativa.

## Etapas

1. Abra [Alertas](#/alerts). Em **Incidentes**, consulte regra, condição, assunto, estado, horário da última observação e código seguro.
2. Em **Entregas**, consulte ID, tipo de notificação, estado, quantidade de tentativas, próxima tentativa e código seguro. Os estados persistidos são **Pending** (aguardando processamento), **Leased** (reservada por um worker), **Delivered** (concluída com sucesso), **Failed** (processamento encerrado sem sucesso) e **Cancelled** (cancelada). A tabela do tenant não exibe o ID do incidente nem um estado “esgotada”.
3. Para uma falha, confira código seguro, política de host/porta, referência do provedor e saúde do receptor. Use o ID para correlacionar os logs do receptor.
4. A interface do tenant permite apenas consultar as entregas e não oferece controle de nova tentativa. Um usuário autorizado com acesso Gerenciar pode solicitar nova tentativa de uma entrega **Failed** pela rota de API `POST /api/alerts/deliveries/{id}/retry`, com `AdditionalAttempts` entre 1 e 20. A tentativa mantém o ID da entrega e respeita os limites. Confira a quantidade e o estado atualizados na tabela do tenant.

Use os filtros de histórico de incidentes e entregas para selecionar período, estado, regra, situação da entrega ou tipo de notificação. Os resultados são ordenados pela atividade mais recente do incidente ou criação da entrega e têm paginação independente. Incidentes Abertos e Desconhecidos continuam visíveis na visão ativa padrão, mesmo quando são anteriores ao período selecionado.

## Exemplo

A tabela mostra uma entrega `Failed` com o código seguro `DestinationDenied`. Confira URL e política `tenant-alerts`, aprove o host e a porta necessários e peça a um usuário autorizado com acesso Gerenciar para solicitar uma nova tentativa pela API suportada. O mesmo ID permite que o receptor correlacione a solicitação repetida.

## Verificação

Confirme se uma tentativa bem-sucedida chegou ao estado **Delivered** e se o incidente reflete a avaliação mais recente. A página mostra códigos seguros, não corpos de resposta, detalhes de exceção ou credenciais. O cabeçalho estável `Idempotency-Key` contém o ID da entrega.

## Recuperação

A entrega ocorre **pelo menos uma vez**, não exatamente uma vez. Um processo pode enviar a solicitação e falhar antes de registrar a confirmação; uma tentativa posterior pode repeti-la. Receptores devem deduplicar por `Idempotency-Key` / ID da entrega. Antes de repetir um resultado incerto, consulte logs e efeitos no sistema de destino. Telemetria ausente pode deixar o incidente Desconhecido; não comprova recuperação.

## Guias relacionados

Consulte [regras de alerta](#/help:alert-rules), [destinos de webhook](#/help:webhook-sinks), [política de saída](#/help:outbound-policy) e [eventos de execução](#/help:execution-events).
