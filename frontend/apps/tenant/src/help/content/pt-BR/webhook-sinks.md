# Configure destinos de webhook

## Pré-requisitos

Você precisa de acesso **Gerenciar**. Permita o host e a porta na [política de saída](#/help:outbound-policy) e configure um provedor confiável para cabeçalhos autenticados. Um destino define um endereço; notificações só são enviadas quando uma regra ativada corresponde a um evento.

## Permissão

Usuários com acesso Gerenciar podem criar, editar, ativar ou desativar destinos. Usuários com acesso de Leitura podem consultar incidentes e resultados, mas não alterar destinos. Não inclua credenciais na URL.

## Etapas

1. Abra [Alertas](#/alerts) e escolha **Novo destino de webhook**. Informe um nome e uma URL HTTP ou HTTPS sem credenciais ou fragmento. Prefira HTTPS.
2. Para cada cabeçalho personalizado, informe o nome e a **referência lógica de segredo**. O provedor a resolve no momento da entrega; não informe o valor. Cabeçalhos de transporte restritos são rejeitados.
3. Defina **Máximo de tentativas** entre 1 e 20, os atrasos mínimo e máximo e tempo limite entre 1 e 120 segundos. O atraso máximo deve ser pelo menos igual ao mínimo.
4. Escolha **Salvar** e revise URL, referências, limite de tentativas e estado. Um destino sozinho não cria alertas; crie uma regra e selecione um destino ativado para encaminhar notificações.

## Exemplo

Para `hooks.example.net`, primeiro permita o host na porta `443` em `tenant-alerts`. Crie **Notificações de operações**, adicione `Authorization` com a referência `tenant/alerts/ops-token` e defina cinco tentativas, atraso inicial de 10 segundos, máximo de 120 segundos e tempo limite de 15 segundos. Mantenha o destino desativado até verificar receptor e provedor.

## Verificação

Confirme se o destino aparece com o estado esperado. Associe-o a uma regra após um teste controlado do receptor. Consulte estado e código de resultado seguro em [incidentes e entregas](#/help:incidents-deliveries); corpos de resposta e credenciais não são exibidos.

## Recuperação

Se a validação falhar, confira formato da URL, nomes únicos de cabeçalho, referências preenchidas, tempo limite e limites de tentativas. Se a entrega for bloqueada, confira host e porta em `tenant-alerts`. Para falhas de segredo, peça ao administrador que verifique referência e identidade de serviço. Recarregue e revise alterações se a versão estiver desatualizada.

## Guias relacionados

Consulte [regras de alerta](#/help:alert-rules), [segredos](#/help:secrets), [política de saída](#/help:outbound-policy) e [incidentes e entregas](#/help:incidents-deliveries).
