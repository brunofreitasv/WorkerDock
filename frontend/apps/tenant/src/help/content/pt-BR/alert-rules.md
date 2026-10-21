# Crie regras de alerta

## Pré-requisitos

Você precisa de acesso **Gerenciar**, de um destino de webhook e de um destino aprovado. Consulte [destinos de webhook](#/help:webhook-sinks) e [política de saída](#/help:outbound-policy). As regras usam o serviço do tenant com SQL e o worker da fila; salvar no modo de memória não comprova que a avaliação está ativa.

## Permissão

Usuários com acesso Gerenciar criam, editam e ativam regras. Usuários com acesso de Leitura podem consultar incidentes e entregas. Uma nova regra é salva desativada para ser revisada antes de abrir incidentes.

![Um destino de webhook ativado é selecionado para uma regra desativada; após a revisão, eventos correspondentes abrem incidentes e o worker da fila tenta entregar.](./media/alert-delivery.svg)

*Figura: A ativação inicia o fluxo de entrega de alertas.* A avaliação depende do serviço SQL e do worker da fila.

## Etapas

1. Abra [Alertas](#/alerts) e escolha **Nova regra**. Informe um nome, selecione um destino ativado e escolha falha repetida, tentativas esgotadas, tempo limite, execução longa, execução perdida ou worker indisponível.
2. Defina o limite e a janela de consulta. Condições de contagem usam o limite dentro da janela. Execução longa e worker indisponível usam a janela como limite de idade e exigem limite `1`. A tolerância atrasa a abertura; um intervalo de lembrete positivo agenda lembretes enquanto o incidente estiver aberto. Zero desativa lembretes.
3. Salve a regra. O primeiro salvamento a cria **desativada**. Revise condição, limite, janela, destino, tolerância e lembretes.
4. Escolha **Ativar regra** somente quando estiver pronta. A ativação é uma ação explícita separada. O processamento depende do serviço SQL e do worker da fila.

## Exemplo

![Formulário de regra de alerta com uma indicação numerada no nome da regra e um destino sintético de operações.](./media/alert-rule.png)

*Figura: Comece com uma regra desativada.* Revise a condição e o destino antes de ativá-la explicitamente.

Para detectar três falhas repetidas em 15 minutos, escolha **Falha repetida**, limite `3`, janela de `900` segundos, um destino de operações ativado e uma tolerância adequada ao processo. Salve, revise a regra desativada e depois ative-a. Para **Execução longa**, use limite `1` e escolha a janela como limite de idade.

## Verificação

Confirme que a regra aparece ativada somente após a ativação. Consulte [incidentes e entregas](#/help:incidents-deliveries) depois que o avaliador observar dados correspondentes. Incidentes são deduplicados por regra, condição e assunto. A ausência de telemetria pode gerar o estado Desconhecido; não comprova recuperação.

## Recuperação

A criação exige um destino ativado. Se a validação falhar, confira os limites inteiros e use limite `1` para execução longa ou worker indisponível. Se o incidente não aparecer, confirme se a avaliação SQL está ativa e se a janela/tolerância passou. Desative a regra durante a investigação de correspondências indesejadas.

## Guias relacionados

Consulte [destinos de webhook](#/help:webhook-sinks), [incidentes e entregas](#/help:incidents-deliveries), [monitoramento](#/help:monitoring) e [configurações do tenant](#/help:settings).
